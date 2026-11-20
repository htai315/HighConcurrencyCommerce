using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Auth;

public sealed class AuthRecoveryFailure(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class RequestPasswordResetRequest
{
    private string email = "";
    [Required, EmailAddress, StringLength(320)]
    public string Email { get => email; set => email = (value ?? "").Trim().ToLowerInvariant(); }
}

public sealed class ResetPasswordRequest : IValidatableObject
{
    [Required, StringLength(128)]
    public string Token { get; set; } = "";

    [Required, StrongPassword]
    public string NewPassword { get; set; } = "";

    [Required, StringLength(128), Compare(nameof(NewPassword), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
    public string ConfirmPassword { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!StrongPasswordAttribute.IsStrong(NewPassword))
            yield return new ValidationResult(new StrongPasswordAttribute().ErrorMessage, [nameof(NewPassword)]);
    }
}

public sealed class ResendVerificationRequest
{
    private string email = "";
    [Required, EmailAddress, StringLength(320)]
    public string Email { get => email; set => email = (value ?? "").Trim().ToLowerInvariant(); }
}

public sealed class ConfirmEmailRequest
{
    [Required, StringLength(128)]
    public string Token { get; set; } = "";
}

public sealed record AuthRecoveryNeutralResponse(string Message);

public interface IAuthRecoveryService
{
    Task<AuthRecoveryNeutralResponse> RequestPasswordResetAsync(RequestPasswordResetRequest request, string correlationId, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, string correlationId, CancellationToken ct = default);
    Task<AuthRecoveryNeutralResponse> ResendVerificationEmailAsync(ResendVerificationRequest request, string correlationId, CancellationToken ct = default);
    Task ConfirmEmailAsync(ConfirmEmailRequest request, string correlationId, CancellationToken ct = default);
}

public interface IAuthEmailSender
{
    Task SendPasswordResetEmailAsync(string recipientEmail, string resetLink, CancellationToken ct = default);
    Task SendVerificationEmailAsync(string recipientEmail, string verificationLink, CancellationToken ct = default);
    Task SendStaffInvitationEmailAsync(string recipientEmail, string invitationLink, CancellationToken ct = default);
}

