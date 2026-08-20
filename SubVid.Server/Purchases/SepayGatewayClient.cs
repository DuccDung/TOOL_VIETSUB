using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SubVid.Server.Purchases;

public sealed class SepayGatewayClient(
    HttpClient httpClient,
    IOptions<SepayOptions> optionsAccessor,
    ILogger<SepayGatewayClient> logger)
{
    private readonly SepayOptions options = optionsAccessor.Value;

    public async Task<SepayCheckoutSnapshot> PrepareCheckoutAsync(
        decimal amount,
        string transferContent,
        CancellationToken cancellationToken)
    {
        var normalizedAmount = NormalizeVndAmount(amount);
        if (string.IsNullOrWhiteSpace(transferContent))
        {
            throw new InvalidOperationException("Nội dung chuyển khoản không hợp lệ.");
        }

        var receiver = await ResolveReceiverAsync(cancellationToken);
        if (!receiver.IsValid)
        {
            throw new InvalidOperationException("Tài khoản nhận tiền SePay chưa được cấu hình đầy đủ.");
        }

        return new SepayCheckoutSnapshot(
            receiver.BankName,
            receiver.BankShortName,
            receiver.AccountNumber,
            receiver.AccountName,
            BuildQrUrl(receiver.AccountNumber, receiver.BankShortName, normalizedAmount, transferContent),
            receiver.ResolvedByApi);
    }

    internal string BuildQrUrl(
        string accountNumber,
        string bankShortName,
        decimal amount,
        string transferContent)
    {
        var baseUrl = options.QrBaseUrl.TrimEnd('/');
        return $"{baseUrl}/img?acc={Uri.EscapeDataString(accountNumber)}"
            + $"&bank={Uri.EscapeDataString(bankShortName)}"
            + $"&amount={Uri.EscapeDataString(amount.ToString("0", CultureInfo.InvariantCulture))}"
            + $"&des={Uri.EscapeDataString(transferContent)}";
    }

    public static decimal NormalizeVndAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new InvalidOperationException("Số tiền thanh toán phải lớn hơn 0.");
        }

        var rounded = decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
        if (rounded != amount)
        {
            throw new InvalidOperationException("Giá gói VND phải là số nguyên.");
        }

        return rounded;
    }

    private async Task<SepayReceiver> ResolveReceiverAsync(CancellationToken cancellationToken)
    {
        var fallback = new SepayReceiver(
            options.ReceiverBankName.Trim(),
            options.ReceiverBankShortName.Trim(),
            options.ReceiverAccountNumber.Trim(),
            options.ReceiverAccountName.Trim(),
            false);
        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return fallback;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildLookupUrl());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken.Trim());
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SePay receiver lookup returned HTTP {StatusCode}; configured fallback will be used.", (int)response.StatusCode);
                return fallback;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return TryResolveReceiver(document.RootElement) ?? fallback;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(exception, "SePay receiver lookup failed; configured fallback will be used.");
            return fallback;
        }
    }

    private string BuildLookupUrl()
    {
        var baseUrl = options.ApiBaseUrl.TrimEnd('/');
        return options.BankAccountId is > 0
            ? $"{baseUrl}/api/v1/bank-accounts/{options.BankAccountId.Value}"
            : $"{baseUrl}/api/v1/bank-accounts";
    }

    private SepayReceiver? TryResolveReceiver(JsonElement root)
    {
        foreach (var candidate in EnumerateCandidates(root))
        {
            var accountNumber = Text(candidate, "account_number", "accountNumber");
            var bankShortName = Text(candidate, "bank_short_name", "bankShortName", "short_name", "shortName");
            if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bankShortName))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(options.ReceiverAccountNumber)
                && !string.Equals(NormalizeAccount(accountNumber), NormalizeAccount(options.ReceiverAccountNumber), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(options.ReceiverBankShortName)
                && !string.Equals(
                    NormalizeBankAlias(bankShortName),
                    NormalizeBankAlias(options.ReceiverBankShortName),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new SepayReceiver(
                Text(candidate, "bank_name", "bankName") ?? bankShortName,
                bankShortName,
                accountNumber,
                Text(candidate, "account_name", "accountName") ?? options.ReceiverAccountName,
                true);
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateCandidates(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var name in new[] { "data", "items", "results" })
            {
                if (!root.TryGetProperty(name, out var nested))
                {
                    continue;
                }

                if (nested.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in nested.EnumerateArray()) yield return item;
                }
                else if (nested.ValueKind == JsonValueKind.Object)
                {
                    yield return nested;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) yield return item;
        }
    }

    private static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return value.ToString().Trim();
            }
        }

        return null;
    }

    internal static string NormalizeAccount(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant();

    internal static string NormalizeBankAlias(string? value)
    {
        var normalized = NormalizeAccount(value);
        return normalized switch
        {
            "MBBANK" or "MBB" or "MILITARYBANK" => "MB",
            _ => normalized,
        };
    }

    public sealed record SepayCheckoutSnapshot(
        string BankName,
        string BankShortName,
        string AccountNumber,
        string AccountName,
        string QrImageUrl,
        bool ResolvedByApi);

    private sealed record SepayReceiver(
        string BankName,
        string BankShortName,
        string AccountNumber,
        string AccountName,
        bool ResolvedByApi)
    {
        public bool IsValid => !string.IsNullOrWhiteSpace(BankName)
            && !string.IsNullOrWhiteSpace(BankShortName)
            && !string.IsNullOrWhiteSpace(AccountNumber)
            && !string.IsNullOrWhiteSpace(AccountName);
    }
}
