using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Auth;

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
