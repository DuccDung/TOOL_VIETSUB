using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;
using SubVid.Server.Purchases;
using SubVid.Server.Registration;
using SubVid.Server.Usage;

var builder = WebApplication.CreateBuilder(args);

ConfigureJwtSigningKey(builder);
ConfigureRegistrationSecrets(builder);
ConfigureSepaySecrets(builder);
var jwtOptions = builder.Configuration
    .GetSection(JwtOptions.SectionName)
    .Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT configuration is missing.");

builder.Services.AddRazorPages();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var traceId = context.HttpContext.TraceIdentifier;
        var firstError = context.ModelState.Values
            .SelectMany(value => value.Errors)
            .Select(error => error.ErrorMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
        return new BadRequestObjectResult(ApiEnvelope<object>.Fail(
            "VALIDATION_FAILED",
            firstError ?? "Dữ liệu gửi lên không hợp lệ.",
            traceId));
    };
});
builder.Services.AddOpenApi();
builder.Services.AddDataProtection()
    .SetApplicationName("SubVid.Server");

var connectionString = builder.Configuration.GetConnectionString("SubVidDatabase")
    ?? throw new InvalidOperationException(
        "Connection string 'SubVidDatabase' was not found.");
builder.Services.AddDbContext<SubVidDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(options => options.SigningKey.Length >= 32, "JWT signing key must contain at least 32 characters.")
    .Validate(options => options.AccessTokenMinutes is >= 5 and <= 60, "Access token lifetime is invalid.")
    .Validate(options => options.RefreshTokenDays is >= 1 and <= 90, "Refresh token lifetime is invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<RegistrationOptions>()
    .Bind(builder.Configuration.GetSection(RegistrationOptions.SectionName))
    .Validate(options => options.OtpSecret.Length >= 32, "Registration OTP secret must contain at least 32 characters.")
    .Validate(options => options.OtpLifetimeMinutes is >= 2 and <= 15, "OTP lifetime is invalid.")
    .Validate(options => options.ResendCooldownSeconds is >= 30 and <= 300, "OTP resend cooldown is invalid.")
    .Validate(options => options.MaxAttempts is >= 3 and <= 10, "OTP attempt limit is invalid.")
    .Validate(options => options.MaxResends is >= 1 and <= 5, "OTP resend limit is invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host), "SMTP host is required.")
    .Validate(options => options.Port is >= 1 and <= 65535, "SMTP port is invalid.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.User), "SMTP user is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Pass), "SMTP password is required.")
    .Validate(options => options.TimeoutSeconds is >= 5 and <= 120, "SMTP timeout is invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<QuotaOptions>()
    .Bind(builder.Configuration.GetSection(QuotaOptions.SectionName))
    .Validate(options => options.ReservationLifetimeMinutes is >= 15 and <= 240,
        "Quota reservation lifetime is invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<CloudAccessOptions>()
    .Bind(builder.Configuration.GetSection(CloudAccessOptions.SectionName))
    .Validate(options => options.ReservationLifetimeMinutes is >= 10 and <= 240,
        "Cloud reservation lifetime is invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<SepayOptions>()
    .Bind(builder.Configuration.GetSection(SepayOptions.SectionName))
    .Validate(options => options.PaymentExpireMinutes is >= 5 and <= 120,
        "SePay payment expiration must be between 5 and 120 minutes.")
    .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _),
        "SePay API base URL is invalid.")
    .Validate(options => Uri.TryCreate(options.QrBaseUrl, UriKind.Absolute, out _),
        "SePay QR base URL is invalid.")
    .Validate(options => PaymentReferenceCodeGenerator.NormalizePrefix(options.TransferCodePrefix) == "SUBVID",
        "SePay transfer code prefix must be SUBVID.")
    .Validate(options => builder.Environment.IsDevelopment()
        || (!string.IsNullOrWhiteSpace(options.WebhookApiKey) && options.HasValidReceiver()),
        "Production requires SePay WebhookApiKey and complete receiver account configuration.")
    .ValidateOnStart();
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = WebAdminAuthenticationDefaults.ApplicationScheme;
        options.DefaultChallengeScheme = WebAdminAuthenticationDefaults.ApplicationScheme;
    })
    .AddPolicyScheme(
        WebAdminAuthenticationDefaults.ApplicationScheme,
        WebAdminAuthenticationDefaults.ApplicationScheme,
        options => options.ForwardDefaultSelector = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                return JwtBearerDefaults.AuthenticationScheme;
            }

            return context.Request.Path.StartsWithSegments("/Admin")
                ? WebAdminAuthenticationDefaults.Scheme
                : WebUserAuthenticationDefaults.Scheme;
        })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                if (principal is null
                    || !principal.TryGetUserId(out var userId)
                    || !principal.TryGetSessionId(out var sessionId))
                {
                    context.Fail("Token claims are invalid.");
                    return;
                }

                var database = context.HttpContext.RequestServices
                    .GetRequiredService<SubVidDbContext>();
                var nowUtc = DateTime.UtcNow;
                var sessionIsActive = await database.AuthSessions.AsNoTracking().AnyAsync(
                    item => item.SessionId == sessionId
                        && item.UserId == userId
                        && item.RevokedAtUtc == null
                        && item.ExpiresAtUtc > nowUtc
                        && item.User.StatusCode == "ACTIVE"
                        && item.User.DeletedAtUtc == null,
                    context.HttpContext.RequestAborted);
                if (!sessionIsActive)
                {
                    context.Fail("The session is no longer active.");
                }
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(
                    "AUTH_REQUIRED",
                    "Vui lòng đăng nhập để tiếp tục.",
                    context.HttpContext.TraceIdentifier));
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(
                    "AUTH_FORBIDDEN",
                    "Tài khoản không có quyền thực hiện thao tác này.",
                    context.HttpContext.TraceIdentifier));
            },
        };
    })
    .AddCookie(WebAdminAuthenticationDefaults.Scheme, options =>
    {
        options.Cookie.Name = "SubVid.Server.ADMIN";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/Admin/Login";
        options.AccessDeniedPath = "/Admin/Denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var userIdText = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var database = context.HttpContext.RequestServices
                .GetRequiredService<SubVidDbContext>();
            var isActiveAdmin = Guid.TryParse(userIdText, out var userId)
                && await database.Users.AsNoTracking().AnyAsync(
                    item => item.UserId == userId
                        && item.RoleCode == "ADMIN"
                        && item.StatusCode == "ACTIVE"
                        && item.DeletedAtUtc == null,
                    context.HttpContext.RequestAborted);
            if (!isActiveAdmin)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(WebAdminAuthenticationDefaults.Scheme);
            }
        };
    })
    .AddCookie(WebUserAuthenticationDefaults.Scheme, options =>
    {
        options.Cookie.Name = "SubVid.ACCOUNT";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var userIdText = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var passwordVersionText = context.Principal?.FindFirstValue(
                WebUserAuthenticationDefaults.PasswordVersionClaim);
            var database = context.HttpContext.RequestServices
                .GetRequiredService<SubVidDbContext>();
            var user = Guid.TryParse(userIdText, out var userId)
                ? await database.Users.AsNoTracking().SingleOrDefaultAsync(
                    item => item.UserId == userId && item.DeletedAtUtc == null,
                    context.HttpContext.RequestAborted)
                : null;
            var isValid = user is not null
                && user.StatusCode == "ACTIVE"
                && long.TryParse(passwordVersionText, out var passwordVersion)
                && passwordVersion == WebAccountAuthService.GetPasswordVersion(user);
            if (!isValid)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(WebUserAuthenticationDefaults.Scheme);
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("registration", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("sepay-webhook", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        await context.HttpContext.Response.WriteAsJsonAsync(
            ApiEnvelope<object>.Fail(
                "RATE_LIMITED",
                "Bạn thao tác quá nhanh. Vui lòng thử lại sau.",
                context.HttpContext.TraceIdentifier),
            cancellationToken);
    };
});

builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddScoped<AdminWebAuthService>();
builder.Services.AddScoped<WebAccountAuthService>();
builder.Services.AddScoped<AdminSubscriptionService>();
builder.Services.AddScoped<AdminPurchaseTestService>();
builder.Services.AddScoped<AdminPurchaseService>();
builder.Services.AddScoped<PurchaseCheckoutService>();
builder.Services.AddScoped<PurchaseSettlementService>();
builder.Services.AddScoped<PaymentReferenceCodeGenerator>();
builder.Services.AddScoped<SepayWebhookService>();
builder.Services.AddHttpClient<SepayGatewayClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SubVid-SePay/1.0");
});
builder.Services.AddScoped<AdminPlanService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<QuotaService>();
builder.Services.AddScoped<CloudCredentialProtector>();
builder.Services.AddScoped<CloudCredentialAllocationService>();
builder.Services.AddScoped<CloudAccessService>();
builder.Services.AddHttpClient<CloudCredentialProbeService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SubVid-Admin-Credential-Probe/1.0");
});
builder.Services.AddScoped<AdminCloudService>();
builder.Services.AddScoped<DevelopmentAdminSeeder>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddScoped<IRegistrationEmailSender, SmtpRegistrationEmailSender>();
builder.Services.AddHostedService<RegistrationCleanupService>();
builder.Services.AddHostedService<QuotaReservationCleanupService>();
builder.Services.AddHostedService<CloudReservationCleanupService>();
builder.Services.AddHostedService<CloudAllocationReconciliationService>();

var app = builder.Build();

if (args.Contains("--run-production-purchase-e2e", StringComparer.OrdinalIgnoreCase))
{
    if (!args.Contains("--confirm-production-data", StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Add --confirm-production-data to acknowledge that the E2E flow writes persistent database rows.");
    }

    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<SubVidDbContext>();
    var expectedDatabase = args
        .FirstOrDefault(item => item.StartsWith("--database=", StringComparison.OrdinalIgnoreCase))?
        .Split('=', 2)[1];
    var actualDatabase = database.Database.GetDbConnection().Database;
    if (string.IsNullOrWhiteSpace(expectedDatabase)
        || !string.Equals(expectedDatabase, actualDatabase, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Pass --database={actualDatabase} to confirm the exact target database.");
    }

    var actorAdminId = await database.Users.AsNoTracking()
        .Where(item => item.RoleCode == "ADMIN"
            && item.StatusCode == "ACTIVE"
            && item.DeletedAtUtc == null)
        .OrderBy(item => item.CreatedAtUtc)
        .Select(item => item.UserId)
        .FirstOrDefaultAsync();
    if (actorAdminId == Guid.Empty)
    {
        throw new InvalidOperationException("No active ADMIN account exists for the E2E audit trail.");
    }

    var service = scope.ServiceProvider.GetRequiredService<AdminPurchaseTestService>();
    var pending = await service.CreatePendingProPurchaseAsync(
        actorAdminId,
        "CLI_E2E",
        CancellationToken.None);
    var paid = await service.ProcessSuccessfulFakeWebhookAsync(
        actorAdminId,
        pending.OrderId,
        "CLI_E2E",
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        database = actualDatabase,
        paid.RunId,
        paid.OrderId,
        paid.OrderNumber,
        paid.UserId,
        paid.Email,
        paid.OrderStatus,
        paid.ActivePlanCode,
        paid.ActivatedSubscriptionId,
        paid.FakeCredentialId,
        paid.FakeCredentialStatus,
        paid.FakeCredentialAllocationMode,
        paid.WebhookCount,
    }));
    return;
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(
            "SERVER_ERROR",
            "Hệ thống gặp lỗi khi xử lý yêu cầu.",
            context.TraceIdentifier));
        return;
    }

    context.Response.Redirect("/Error");
}));

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorPages().WithStaticAssets();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DevelopmentAdminSeeder>().SeedAsync();
}

app.Run();

static void ConfigureJwtSigningKey(WebApplicationBuilder builder)
{
    var environmentKey = Environment.GetEnvironmentVariable("SUBVID_JWT_SIGNING_KEY");
    if (!string.IsNullOrWhiteSpace(environmentKey))
    {
        builder.Configuration[$"{JwtOptions.SectionName}:SigningKey"] = environmentKey;
        return;
    }

    if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{JwtOptions.SectionName}:SigningKey"]))
    {
        return;
    }

    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Set SUBVID_JWT_SIGNING_KEY before starting the Server.");
    }

    var keyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SubVid.Server");
    var keyPath = Path.Combine(keyDirectory, "jwt-development.key");
    Directory.CreateDirectory(keyDirectory);
    var signingKey = File.Exists(keyPath)
        ? File.ReadAllText(keyPath).Trim()
        : Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    if (!File.Exists(keyPath))
    {
        File.WriteAllText(keyPath, signingKey);
    }

    builder.Configuration[$"{JwtOptions.SectionName}:SigningKey"] = signingKey;
}

static void ConfigureRegistrationSecrets(WebApplicationBuilder builder)
{
    var otpSecret = Environment.GetEnvironmentVariable("SUBVID_REGISTRATION_OTP_SECRET");
    if (!string.IsNullOrWhiteSpace(otpSecret))
    {
        builder.Configuration[$"{RegistrationOptions.SectionName}:OtpSecret"] = otpSecret;
    }

    var smtpUser = Environment.GetEnvironmentVariable("SUBVID_SMTP_USER");
    if (!string.IsNullOrWhiteSpace(smtpUser))
    {
        builder.Configuration[$"{SmtpOptions.SectionName}:User"] = smtpUser;
    }

    var smtpPassword = Environment.GetEnvironmentVariable("SUBVID_SMTP_PASSWORD");
    if (!string.IsNullOrWhiteSpace(smtpPassword))
    {
        builder.Configuration[$"{SmtpOptions.SectionName}:Pass"] = smtpPassword;
    }
}

static void ConfigureSepaySecrets(WebApplicationBuilder builder)
{
    var mappings = new Dictionary<string, string>
    {
        ["SUBVID_SEPAY_API_TOKEN"] = "ApiToken",
        ["SUBVID_SEPAY_WEBHOOK_API_KEY"] = "WebhookApiKey",
        ["SUBVID_SEPAY_RECEIVER_BANK_SHORT_NAME"] = "ReceiverBankShortName",
        ["SUBVID_SEPAY_RECEIVER_BANK_NAME"] = "ReceiverBankName",
        ["SUBVID_SEPAY_RECEIVER_ACCOUNT_NUMBER"] = "ReceiverAccountNumber",
        ["SUBVID_SEPAY_RECEIVER_ACCOUNT_NAME"] = "ReceiverAccountName",
    };
    foreach (var mapping in mappings)
    {
        var value = Environment.GetEnvironmentVariable(mapping.Key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Configuration[$"{SepayOptions.SectionName}:{mapping.Value}"] = value;
        }
    }

    var bankAccountId = Environment.GetEnvironmentVariable("SUBVID_SEPAY_BANK_ACCOUNT_ID");
    if (int.TryParse(bankAccountId, out var parsedBankAccountId) && parsedBankAccountId > 0)
    {
        builder.Configuration[$"{SepayOptions.SectionName}:BankAccountId"] = parsedBankAccountId.ToString();
    }
}
