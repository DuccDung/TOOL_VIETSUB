using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Auth;

public sealed class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(
        User user,
        Guid sessionId,
        DateTime nowUtc)
    {
        var expiresAtUtc = nowUtc.AddMinutes(_options.AccessTokenMinutes);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.RoleCode),
            new Claim("sid", sessionId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            nowUtc,
            expiresAtUtc,
            credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    public (string Token, byte[] Hash, DateTime ExpiresAtUtc) CreateRefreshToken(DateTime nowUtc)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(64);
        var token = Base64UrlEncoder.Encode(tokenBytes);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return (token, hash, nowUtc.AddDays(_options.RefreshTokenDays));
    }

    public static byte[] HashRefreshToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
