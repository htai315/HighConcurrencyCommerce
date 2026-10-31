using System.Security.Claims;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Api.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(IAuthService auth, SessionCookie cookie, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => Ok(new { requestToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

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
        User.FindFirstValue(ClaimTypes.Email)!, User.FindFirstValue(ClaimTypes.Name)!, User.FindFirstValue(ClaimTypes.Role)!));

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

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/accounts")]
public sealed class AccountStatusController(IAuthService auth) : ControllerBase
{
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, AccountStatusRequest request, CancellationToken ct)
    {
        await auth.SetStatusAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), id, request.Status, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
}

