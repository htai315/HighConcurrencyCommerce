using HighConcurrencyCommerce.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthRecoveryController(IAuthRecoveryService recoveryService) : ControllerBase
{
    [HttpPost("password-reset/request"), AllowAnonymous, EnableRateLimiting("password-reset-request")]
    [ProducesResponseType(typeof(AuthRecoveryNeutralResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetRequest request, CancellationToken ct)
    {
        var response = await recoveryService.RequestPasswordResetAsync(request, HttpContext.TraceIdentifier, ct);
        return Accepted(response);
    }

    [HttpPost("password-reset/confirm"), AllowAnonymous, EnableRateLimiting("auth-recovery-confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await recoveryService.ResetPasswordAsync(request, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }

    [HttpPost("email-verification/resend"), AllowAnonymous, EnableRateLimiting("email-verification-resend")]
    [ProducesResponseType(typeof(AuthRecoveryNeutralResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendVerificationEmail([FromBody] ResendVerificationRequest request, CancellationToken ct)
    {
        var response = await recoveryService.ResendVerificationEmailAsync(request, HttpContext.TraceIdentifier, ct);
        return Accepted(response);
    }

    [HttpPost("email-verification/confirm"), AllowAnonymous, EnableRateLimiting("auth-recovery-confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request, CancellationToken ct)
    {
        await recoveryService.ConfirmEmailAsync(request, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
}

