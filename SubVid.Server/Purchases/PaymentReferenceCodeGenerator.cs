using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubVid.Server.Data;

namespace SubVid.Server.Purchases;

public sealed class PaymentReferenceCodeGenerator(
    SubVidDbContext database,
    IOptions<SepayOptions> options)
{
    public const int DigitCount = 10;

    public async Task<string> GenerateAsync(CancellationToken cancellationToken)
    {
        var prefix = NormalizePrefix(options.Value.TransferCodePrefix);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = CreateCandidate(prefix);
            if (!await database.PurchasePaymentTransactions.AsNoTracking()
                    .AnyAsync(item => item.TransactionCode == candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Không thể tạo mã chuyển khoản duy nhất. Vui lòng thử lại.");
    }

    public static string NormalizePrefix(string? value)
    {
        var prefix = string.Concat((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit));
        return prefix.Length is >= 3 and <= 20 ? prefix : "SUBVID";
    }

    public static string CreateCandidate(string prefix = "SUBVID")
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var digits = new char[DigitCount];
        for (var index = 0; index < digits.Length; index++)
        {
            digits[index] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return $"{normalizedPrefix}-{new string(digits)}";
    }
}
