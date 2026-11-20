using System.ComponentModel.DataAnnotations;
using HighConcurrencyCommerce.Application.Auth;

namespace HighConcurrencyCommerce.Application.AdminAccounts;

public sealed record AdminAccountListItemDto(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    string AccountStatus,
    string LifecycleStatus,
    DateTime? EmailVerifiedAt,
    DateTime CreatedAt,
    int ActiveSessionCount,
    DateTime? LatestSessionAt
);

public sealed record AdminAccountDetailDto(
    Guid Id,
    string Email,
    string FullName,
    string? Phone,
    string Role,
    string AccountStatus,
    string LifecycleStatus,
    DateTime? EmailVerifiedAt,
    DateTime? LockedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int ActiveSessionCount,
    DateTime? LatestSessionAt,
    DateTime? LatestSessionExpiresAt,
    bool HasPassword,
    bool HasGoogleIdentity
);

public sealed record AdminAccountPageDto(
    IReadOnlyList<AdminAccountListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount
);

public sealed record AdminAccountQuery
{
    [MaxLength(100)]
    public string? Q { get; init; }

    [RegularExpression("^(CUSTOMER|STAFF|ADMIN)$", ErrorMessage = "Role không hợp lệ.")]
    public string? Role { get; init; }

    [RegularExpression("^(ACTIVE|LOCKED)$", ErrorMessage = "Status không hợp lệ.")]
    public string? Status { get; init; }

    [RegularExpression("^(INVITATION_PENDING|ACTIVE|LOCKED)$", ErrorMessage = "Lifecycle không hợp lệ.")]
    public string? Lifecycle { get; init; }

    [Range(1, 100_000, ErrorMessage = "Trang phải từ 1 đến 100.000.")]
    public int Page { get; init; } = 1;

    [Range(1, 100, ErrorMessage = "Kích thước trang phải từ 1 đến 100.")]
    public int PageSize { get; init; } = 25;
}

public sealed class InviteStaffRequest
{
    [Required(ErrorMessage = "Email không được để trống.")]
    [EmailAddress(ErrorMessage = "Định dạng email không hợp lệ.")]
    [StringLength(320, ErrorMessage = "Email tối đa 320 ký tự.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Họ tên không được để trống.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ tên từ 2 đến 100 ký tự.")]
    public string FullName { get; set; } = "";
}

public sealed class AcceptStaffInvitationRequest : IValidatableObject
{
    [Required(ErrorMessage = "Token không được để trống.")]
    [StringLength(128, ErrorMessage = "Token tối đa 128 ký tự.")]
    public string Token { get; set; } = "";

    [Required(ErrorMessage = "Mật khẩu không được để trống.")]
    [StrongPassword]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "Xác nhận mật khẩu không được để trống.")]
    [StringLength(128)]
    [Compare(nameof(Password), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
    public string ConfirmPassword { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!StrongPasswordAttribute.IsStrong(Password))
            yield return new ValidationResult(new StrongPasswordAttribute().ErrorMessage, [nameof(Password)]);
    }
}

public sealed class AdminAccountFailure : Exception
{
    public string Code { get; }
    public int Status { get; }

    public AdminAccountFailure(string code, string message, int status = 400) : base(message)
    {
        Code = code;
        Status = status;
    }
}

public interface IAdminAccountService
{
    Task<AdminAccountPageDto> GetAccountsAsync(AdminAccountQuery query, CancellationToken ct = default);
    Task<AdminAccountDetailDto> GetAccountByIdAsync(Guid id, CancellationToken ct = default);
    Task<AdminAccountDetailDto> InviteStaffAsync(Guid actorId, InviteStaffRequest request, string correlationId, CancellationToken ct = default);
    Task ResendStaffInvitationAsync(Guid actorId, Guid targetId, string correlationId, CancellationToken ct = default);
    Task SetAccountStatusAsync(Guid actorId, Guid targetId, string status, string correlationId, CancellationToken ct = default);
    Task RevokeAccountSessionsAsync(Guid actorId, Guid targetId, string correlationId, CancellationToken ct = default);
    Task AcceptStaffInvitationAsync(AcceptStaffInvitationRequest request, string correlationId, CancellationToken ct = default);
}

