using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubVid.Server.Contracts;
using SubVid.Server.Controllers;
using SubVid.Server.Purchases;

namespace SubVid.App.Tests;

public sealed class SepayPaymentTests
{
    [Fact]
    public void CreateCandidate_ProducesSecureShapeAndNoDuplicatesInLargeSample()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 10_000; index++)
        {
            var value = PaymentReferenceCodeGenerator.CreateCandidate();
            Assert.Matches("^SUBVID-[0-9]{10}$", value);
            Assert.True(values.Add(value));
        }
    }

    [Theory]
    [InlineData("SUBVID-1234567890")]
    [InlineData("SUBVID1234567890")]
    [InlineData("SUBVID 1234567890")]
    [InlineData("SUBVID_1234567890")]
    [InlineData("subvid - 1234567890")]
    public void ExtractTransferCodes_NormalizesSupportedVariants(string input)
    {
        var result = SepayWebhookService.ExtractTransferCodes("SUBVID", input);
        Assert.Equal(["SUBVID-1234567890"], result);
    }

    [Theory]
    [InlineData("OTHER-1234567890")]
    [InlineData("SUBVID-123456789")]
    [InlineData("SUBVID-12345678901")]
    [InlineData("SUBVID-ABCDEFGHIJ")]
    public void ExtractTransferCodes_RejectsWrongPrefixOrDigitCount(string input)
    {
        Assert.Empty(SepayWebhookService.ExtractTransferCodes("SUBVID", input));
    }

    [Fact]
    public void ExtractTransferCodes_PreservesAmbiguity()
    {
        var result = SepayWebhookService.ExtractTransferCodes(
            "SUBVID",
            "SUBVID-1111111111 và SUBVID 2222222222");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Gateway_UsesApiReceiverAndEncodesQrParameters()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"data":[{"bank_name":"Test Bank","bank_short_name":"TB","account_number":"001 234","account_name":"SUBVID API"}]}
            """);
        var options = CreateOptions(
            apiToken: "test-token",
            receiverAccountNumber: "001 234",
            receiverBankShortName: "TB");
        var gateway = new SepayGatewayClient(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<SepayGatewayClient>.Instance);

        var result = await gateway.PrepareCheckoutAsync(199_000m, "SUBVID-1234567890 TEST", CancellationToken.None);

        Assert.True(result.ResolvedByApi);
        Assert.Equal("001 234", result.AccountNumber);
        Assert.Contains("acc=001%20234", result.QrImageUrl);
        Assert.Contains("bank=TB", result.QrImageUrl);
        Assert.Contains("amount=199000", result.QrImageUrl);
        Assert.Contains("des=SUBVID-1234567890%20TEST", result.QrImageUrl);
    }

    [Fact]
    public async Task Gateway_FallsBackWhenApiFails()
    {
        var gateway = new SepayGatewayClient(
            new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable, "unavailable")),
            Options.Create(CreateOptions(apiToken: "test-token")),
            NullLogger<SepayGatewayClient>.Instance);

        var result = await gateway.PrepareCheckoutAsync(99_000m, "SUBVID-1234567890", CancellationToken.None);

        Assert.False(result.ResolvedByApi);
        Assert.Equal("0000000000", result.AccountNumber);
        Assert.Equal("SUBVID TEST", result.AccountName);
    }

    [Fact]
    public void ExternalEventId_IsStableWhenProviderIdentifiersAreMissing()
    {
        var payload = new SepayWebhookPayload
        {
            TransferType = "in",
            TransferAmount = 100_000,
            Content = "SUBVID-1234567890",
        };
        const string canonical = "canonical-payload";

        var first = SepayWebhookService.ResolveExternalEventId(payload, canonical);
        var second = SepayWebhookService.ResolveExternalEventId(payload, canonical);

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first);
    }

    [Fact]
    public void CheckoutResponse_DoesNotExposeSecretsOrRawProviderPayload()
    {
        var propertyNames = typeof(PurchaseCheckoutResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Connection", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void NormalizeVndAmount_RejectsNonPositiveOrFractionalValues(decimal value)
    {
        Assert.Throws<InvalidOperationException>(() => SepayGatewayClient.NormalizeVndAmount(value));
    }

    [Fact]
    public void NativeCheckoutRequestContract_CarriesNoUserIdentityOrToken()
    {
        var request = new SubVid.App.Api.CreatePurchaseCheckoutApiRequest(
            "PRO",
            "checkout-unit-test",
            199_000m);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var propertyNames = typeof(SubVid.App.Api.CreatePurchaseCheckoutApiRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("\"planCode\":\"PRO\"", json);
        Assert.Contains("\"idempotencyKey\":\"checkout-unit-test\"", json);
        Assert.Contains("\"expectedPriceAmount\":199000", json);
        Assert.DoesNotContain(propertyNames, name => name.Contains("User", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WebhookPayloadValidation_RejectsMissingRequiredShape()
    {
        Assert.False(PaymentsController.IsStructurallyValid(null));
        Assert.False(PaymentsController.IsStructurallyValid(new SepayWebhookPayload()));
        Assert.False(PaymentsController.IsStructurallyValid(new SepayWebhookPayload
        {
            TransferType = "in",
        }));
        Assert.True(PaymentsController.IsStructurallyValid(new SepayWebhookPayload
        {
            TransferType = "in",
            AccountNumber = "0000000000",
        }));
    }

    private static SepayOptions CreateOptions(
        string apiToken,
        string receiverAccountNumber = "0000000000",
        string receiverBankShortName = "FB") => new()
    {
        ApiToken = apiToken,
        BankAccountId = 1,
        ReceiverBankName = "Fallback Bank",
        ReceiverBankShortName = receiverBankShortName,
        ReceiverAccountNumber = receiverAccountNumber,
        ReceiverAccountName = "SUBVID TEST",
        WebhookApiKey = "unit-test-webhook-key",
    };

    private sealed class StubHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        });
    }
}
