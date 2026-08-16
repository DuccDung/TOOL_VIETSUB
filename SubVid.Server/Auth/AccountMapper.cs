using SubVid.Server.Contracts;
using SubVid.Server.Models;

namespace SubVid.Server.Auth;

public static class AccountMapper
{
    public static AccountResponse ToResponse(User user) => new(
        user.UserId,
        user.Email,
        user.DisplayName,
        user.RoleCode,
        user.StatusCode,
        user.EmailConfirmed,
        user.CreatedAtUtc,
        user.LastLoginAtUtc);
}
