using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Auth;

public sealed record AuthUser(Guid Id, string Email, string FullName, string Role, bool EmailVerified);
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

public sealed record GoogleUserPayload(
    string Subject,
    string Email,
    bool EmailVerified,
    string? Name,
    string? GivenName,
    string? FamilyName,
    string? Nonce
);

public interface IGoogleTokenVerifier
{
    Task<GoogleUserPayload> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct);
}

public sealed class GoogleChallengeRequest
{
    [Required, RegularExpression("^(LOGIN|LINK)$")]
    public string Purpose { get; set; } = "LOGIN";
}

public sealed record GoogleChallengeResponse(string ChallengeId, string Nonce);

public sealed class GoogleLoginRequest
{
    [Required]
    public string Credential { get; set; } = "";

    [Required]
    public Guid ChallengeId { get; set; }
}

public sealed record AuthProviderInfo(bool Enabled, string? ClientId);
public sealed record AuthProvidersResponse(AuthProviderInfo Google);

public sealed record AuthMethodsResponse(bool HasPassword, bool GoogleLinked, string? GoogleEmail);

public sealed class LinkGoogleRequest
{
    public string? CurrentPassword { get; set; }

    [Required]
    public string Credential { get; set; } = "";

    [Required]
    public Guid ChallengeId { get; set; }
}

public sealed class UnlinkGoogleRequest
{
    [Required, StringLength(128)]
    public string CurrentPassword { get; set; } = "";
}

public interface IAuthService
{
    Task RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<SessionTicket> LoginAsync(LoginRequest request, string? previousToken, CancellationToken ct);
    Task<AuthUser?> FindSessionAsync(string token, CancellationToken ct);
    Task LogoutAsync(string? token, CancellationToken ct);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct);
    Task SetStatusAsync(Guid actorId, Guid targetId, string status, string correlationId, CancellationToken ct);

    AuthProvidersResponse GetProviders();
    Task<GoogleChallengeResponse> CreateGoogleChallengeAsync(string purpose, Guid? userId, string challengeToken, CancellationToken ct);
    Task<SessionTicket> GoogleLoginAsync(GoogleLoginRequest request, string challengeToken, string? previousSessionToken, string correlationId, CancellationToken ct);
    Task<AuthMethodsResponse> GetAuthMethodsAsync(Guid userId, CancellationToken ct);
    Task LinkGoogleAsync(Guid userId, LinkGoogleRequest request, string challengeToken, string correlationId, CancellationToken ct);
    Task UnlinkGoogleAsync(Guid userId, UnlinkGoogleRequest request, string correlationId, CancellationToken ct);
}
