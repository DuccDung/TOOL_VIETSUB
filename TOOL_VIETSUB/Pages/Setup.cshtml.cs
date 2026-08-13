using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Pages;

public sealed class SetupModel(
    ToolVietSubDbContext database,
    IPasswordHasher<User> passwordHasher) : PageModel
{
    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await database.Users.AsNoTracking().AnyAsync(
            item => item.DeletedAtUtc == null,
            cancellationToken)
            ? RedirectToPage("/Index")
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Input.Password, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.ConfirmPassword", "Mật khẩu xác nhận không khớp.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (await database.Users.AnyAsync(item => item.DeletedAtUtc == null, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return RedirectToPage("/Index");
        }

        var proPlan = await database.ServicePlans.SingleAsync(
            item => item.PlanCode == "PRO" && item.IsActive,
            cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = Input.Email.Trim(),
            DisplayName = Input.DisplayName.Trim(),
            RoleCode = "ADMIN",
            StatusCode = "ACTIVE",
            EmailConfirmed = true,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, Input.Password);
        database.Users.Add(user);
        database.UserSubscriptions.Add(new UserSubscription
        {
            SubscriptionId = Guid.NewGuid(),
            UserId = user.UserId,
            PlanId = proPlan.PlanId,
            StatusCode = "ACTIVE",
            StartsAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RedirectToPage("/Index", new { setup = "complete" });
    }

    public sealed class SetupInput
    {
        [Required(ErrorMessage = "Hãy nhập tên hiển thị.")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Tên hiển thị phải có từ 2 đến 200 ký tự.")]
        [Display(Name = "Tên hiển thị")]
        public string DisplayName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập email.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        [StringLength(320)]
        [Display(Name = "Email quản trị")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập mật khẩu.")]
        [StringLength(256, MinimumLength = 12, ErrorMessage = "Mật khẩu phải có ít nhất 12 ký tự.")]
        [DataType(DataType.Password)]
        [Display(Name = "Mật khẩu")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy xác nhận mật khẩu.")]
        [DataType(DataType.Password)]
        [Display(Name = "Xác nhận mật khẩu")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
