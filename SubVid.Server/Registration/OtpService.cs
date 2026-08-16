using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SubVid.Server.Registration;

public sealed class OtpService(IOptions<RegistrationOptions> options)
{
    private readonly byte[] _secret = DecodeSecret(options.Value.OtpSecret);

    public string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public byte[] HashCode(Guid challengeId, string normalizedEmail, string code)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{challengeId:N}:{normalizedEmail}:{code}");
        return HMACSHA256.HashData(_secret, payload);
    }

    public bool VerifyCode(
        Guid challengeId,
        string normalizedEmail,
        string code,
        byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            HashCode(challengeId, normalizedEmail, code),
            expectedHash);

    private static byte[] DecodeSecret(string configuredSecret)
    {
        try
        {
            return Convert.FromBase64String(configuredSecret);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(configuredSecret);
        }
    }
}
