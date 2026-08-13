using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace TOOL_VIETSUB.Auth;

public static class ClaimsPrincipalExtensions
{
    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);

    public static bool TryGetSessionId(this ClaimsPrincipal principal, out Guid sessionId) =>
        Guid.TryParse(principal.FindFirstValue("sid"), out sessionId);
}
