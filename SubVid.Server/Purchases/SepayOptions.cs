namespace SubVid.Server.Purchases;

public sealed class SepayOptions
{
    public const string SectionName = "SePay";

    public string ApiBaseUrl { get; set; } = "https://my.sepay.vn";

    public string ApiToken { get; set; } = string.Empty;

    public int? BankAccountId { get; set; }

    public string QrBaseUrl { get; set; } = "https://qr.sepay.vn";

    public string ReceiverBankShortName { get; set; } = string.Empty;

    public string ReceiverBankName { get; set; } = string.Empty;

    public string ReceiverAccountNumber { get; set; } = string.Empty;

    public string ReceiverAccountName { get; set; } = string.Empty;

    public string WebhookApiKey { get; set; } = string.Empty;

    public string TransferCodePrefix { get; set; } = "SUBVID";

    public int PaymentExpireMinutes { get; set; } = 15;

    public bool HasValidReceiver() =>
        !string.IsNullOrWhiteSpace(ReceiverBankShortName)
        && !string.IsNullOrWhiteSpace(ReceiverBankName)
        && !string.IsNullOrWhiteSpace(ReceiverAccountNumber)
        && !string.IsNullOrWhiteSpace(ReceiverAccountName);
}
