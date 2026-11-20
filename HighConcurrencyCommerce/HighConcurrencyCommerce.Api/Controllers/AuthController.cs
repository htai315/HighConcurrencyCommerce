using System.Security.Claims;
using System.Security.Cryptography;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Api.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    IAuthService auth,
    SessionCookie cookie,
    ChallengeCookie challengeCookie,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => Ok(new { requestToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpGet("providers"), AllowAnonymous]
    public ActionResult<AuthProvidersResponse> Providers() => Ok(auth.GetProviders());

    [HttpPost("google/challenge"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<ActionResult<GoogleChallengeResponse>> GoogleChallenge(GoogleChallengeRequest request, CancellationToken ct)
    {
        Guid? userId = null;
        if (request.Purpose == "LINK")
        {
            if (User.Identity?.IsAuthenticated != true)
                return Unauthorized();
            userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }

        var challengeToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var response = await auth.CreateGoogleChallengeAsync(request.Purpose, userId, challengeToken, ct);
        challengeCookie.Write(HttpContext, challengeToken, DateTime.UtcNow.AddMinutes(5));
        return Ok(response);
    }

    [HttpPost("google"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<ActionResult<AuthUser>> GoogleLogin(GoogleLoginRequest request, CancellationToken ct)
    {
        var challengeToken = challengeCookie.Read(HttpContext) ?? "";
        var session = await auth.GoogleLoginAsync(request, challengeToken, cookie.Read(HttpContext), HttpContext.TraceIdentifier, ct);
        challengeCookie.Delete(HttpContext);
        cookie.Write(HttpContext, session);
        return Ok(session.User);
    }

    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        await auth.RegisterAsync(request, ct);
        return Accepted(new { message = "Nếu thông tin hợp lệ, bạn có thể đăng nhập bằng email và mật khẩu đã chọn." });
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<ActionResult<AuthUser>> Login(LoginRequest request, CancellationToken ct)
    {
        var session = await auth.LoginAsync(request, cookie.Read(HttpContext), ct);
        cookie.Write(HttpContext, session);
        return Ok(session.User);
    }

    [HttpGet("me")]
    public ActionResult<AuthUser> Me() => Ok(new AuthUser(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        User.FindFirstValue(ClaimTypes.Email)!, User.FindFirstValue(ClaimTypes.Name)!, User.FindFirstValue(ClaimTypes.Role)!,
        string.Equals(User.FindFirstValue("email_verified"), "true", StringComparison.Ordinal)));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(cookie.Read(HttpContext), ct);
        cookie.Delete(HttpContext);
        return NoContent();
    }

    [HttpPost("password"), EnableRateLimiting("password")]
    public async Task<IActionResult> Password(ChangePasswordRequest request, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), request, ct);
        cookie.Delete(HttpContext);
        return NoContent();
    }
}

