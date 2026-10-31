using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Auth;

public sealed record AuthUser(Guid Id, string Email, string FullName, string Role);
public sealed record SessionTicket(string Token, DateTime ExpiresAt, AuthUser User);
public sealed class AuthFailure(string code) : Exception(code) { public string Code { get; } = code; }

public class LoginRequest
{
    private string email = "";
    [Required, EmailAddress, StringLength(320)]
    public string Email { get => email; set => email = (value ?? "").Trim().ToLowerInvariant(); }
    [Required, StringLength(128)] public string Password { get; set; } = "";
}
public sealed class RegisterRequest : LoginRequest, IValidatableObject
{
    [Required, StringLength(160)] public string FullName { get; set; } = "";
    [Required, StringLength(128), Compare(nameof(Password), ErrorMessage = "Mật khẩu nhập lại chưa khớp.")]
    public string ConfirmPassword { get; set; } = "";
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!StrongPasswordAttribute.IsStrong(Password)) yield return new ValidationResult(new StrongPasswordAttribute().ErrorMessage, [nameof(Password)]);
    }
}
public sealed class ChangePasswordRequest
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
    [Required, StrongPassword] public string NewPassword { get; set; } = "";
}
public sealed class AccountStatusRequest
{
    [Required, RegularExpression("^(ACTIVE|LOCKED)$")] public string Status { get; set; } = "";
}
public interface IAuthService
{
    Task RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<SessionTicket> LoginAsync(LoginRequest request, string? previousToken, CancellationToken ct);
    Task<AuthUser?> FindSessionAsync(string token, CancellationToken ct);
    Task LogoutAsync(string? token, CancellationToken ct);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct);
    Task SetStatusAsync(Guid actorId, Guid targetId, string status, string correlationId, CancellationToken ct);
}

