using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TOOL_VIETSUB.Auth;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;
using TOOL_VIETSUB.Registration;
using TOOL_VIETSUB.Usage;

var builder = WebApplication.CreateBuilder(args);

ConfigureJwtSigningKey(builder);
ConfigureRegistrationSecrets(builder);
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

var connectionString = builder.Configuration.GetConnectionString("ToolVietSubDatabase")
    ?? throw new InvalidOperationException(
        "Connection string 'ToolVietSubDatabase' was not found.");
builder.Services.AddDbContext<ToolVietSubDbContext>(options =>
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
            context.Request.Path.StartsWithSegments("/api")
                ? JwtBearerDefaults.AuthenticationScheme
                : WebAdminAuthenticationDefaults.Scheme)
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
                    .GetRequiredService<ToolVietSubDbContext>();
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
        options.Cookie.Name = "TOOL_VIETSUB.ADMIN";
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
                .GetRequiredService<ToolVietSubDbContext>();
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
builder.Services.AddScoped<AdminSubscriptionService>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<QuotaService>();
builder.Services.AddScoped<DevelopmentAdminSeeder>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddScoped<IRegistrationEmailSender, SmtpRegistrationEmailSender>();
builder.Services.AddHostedService<RegistrationCleanupService>();
builder.Services.AddHostedService<QuotaReservationCleanupService>();

var app = builder.Build();

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
    var environmentKey = Environment.GetEnvironmentVariable("TOOL_VIETSUB_JWT_SIGNING_KEY");
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
            "Set TOOL_VIETSUB_JWT_SIGNING_KEY before starting the Server.");
    }

    var keyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TOOL_VIETSUB_SERVER");
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
    var otpSecret = Environment.GetEnvironmentVariable("TOOL_VIETSUB_REGISTRATION_OTP_SECRET");
    if (!string.IsNullOrWhiteSpace(otpSecret))
    {
        builder.Configuration[$"{RegistrationOptions.SectionName}:OtpSecret"] = otpSecret;
    }

    var smtpUser = Environment.GetEnvironmentVariable("TOOL_VIETSUB_SMTP_USER");
    if (!string.IsNullOrWhiteSpace(smtpUser))
    {
        builder.Configuration[$"{SmtpOptions.SectionName}:User"] = smtpUser;
    }

    var smtpPassword = Environment.GetEnvironmentVariable("TOOL_VIETSUB_SMTP_PASSWORD");
    if (!string.IsNullOrWhiteSpace(smtpPassword))
    {
        builder.Configuration[$"{SmtpOptions.SectionName}:Pass"] = smtpPassword;
    }
}
