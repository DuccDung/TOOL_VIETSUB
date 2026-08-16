using SubVid.Server.Contracts;

namespace SubVid.Server.Auth;

public sealed record AuthServiceResult(
    TokenPairResponse? Tokens,
    string? ErrorCode,
    string? ErrorMessage,
    bool Forbidden = false)
{
    public bool Succeeded => Tokens is not null;

    public static AuthServiceResult Success(TokenPairResponse tokens) =>
        new(tokens, null, null);

    public static AuthServiceResult Failure(
        string code,
        string message,
        bool forbidden = false) =>
        new(null, code, message, forbidden);
}
