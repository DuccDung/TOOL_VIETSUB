namespace TOOL_VIETSUB.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = "TOOL_VIETSUB_SERVER";

    public string Audience { get; init; } = "TOOL_VIETSUB_APP";

    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 15;

    public int RefreshTokenDays { get; init; } = 30;
}
