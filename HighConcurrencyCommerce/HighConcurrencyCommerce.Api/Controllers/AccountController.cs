using System.Security.Claims;
using HighConcurrencyCommerce.Application.Account;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/v1/account")]
public sealed class AccountController(
    IAccountService accountService,
    IAuthService auth,
    SessionCookie cookie,
    ChallengeCookie challengeCookie) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("auth-methods")]
    public async Task<ActionResult<AuthMethodsResponse>> AuthMethods(CancellationToken ct) =>
        Ok(await auth.GetAuthMethodsAsync(CustomerId, ct));

    [HttpPost("auth-methods/google"), EnableRateLimiting("password")]
    public async Task<IActionResult> LinkGoogle(LinkGoogleRequest request, CancellationToken ct)
    {
        var challengeToken = challengeCookie.Read(HttpContext) ?? "";
        await auth.LinkGoogleAsync(CustomerId, request, challengeToken, HttpContext.TraceIdentifier, ct);
        challengeCookie.Delete(HttpContext);
        return NoContent();
    }

    [HttpDelete("auth-methods/google"), EnableRateLimiting("password")]
    public async Task<IActionResult> UnlinkGoogle(UnlinkGoogleRequest request, CancellationToken ct)
    {
        await auth.UnlinkGoogleAsync(CustomerId, request, HttpContext.TraceIdentifier, ct);
        cookie.Delete(HttpContext);
        return NoContent();
    }

    [HttpGet("profile")]
    public async Task<ActionResult<CustomerProfileDto>> Profile(CancellationToken ct) =>
        Ok(await accountService.GetProfileAsync(CustomerId, ct));

    [HttpPatch("profile")]
    public async Task<ActionResult<CustomerProfileDto>> UpdateProfile(UpdateProfileRequest request, CancellationToken ct) =>
        Ok(await accountService.UpdateProfileAsync(CustomerId, request, ct));

    [HttpGet("addresses")]
    public async Task<ActionResult<IReadOnlyList<AddressDto>>> Addresses(CancellationToken ct) =>
        Ok(await accountService.GetAddressesAsync(CustomerId, ct));

    [HttpGet("addresses/{id:guid}", Name = nameof(GetAddress))]
    public async Task<ActionResult<AddressDto>> GetAddress(Guid id, CancellationToken ct) =>
        Ok(await accountService.GetAddressAsync(CustomerId, id, ct));

    [HttpPost("addresses")]
    public async Task<ActionResult<AddressDto>> CreateAddress(CreateAddressRequest request, CancellationToken ct)
    {
        var address = await accountService.CreateAddressAsync(CustomerId, request, ct);
        return CreatedAtRoute(nameof(GetAddress), new { id = address.Id }, address);
    }

    [HttpPatch("addresses/{id:guid}")]
    public async Task<ActionResult<AddressDto>> UpdateAddress(Guid id, UpdateAddressRequest request, CancellationToken ct) =>
        Ok(await accountService.UpdateAddressAsync(CustomerId, id, request, ct));

    [HttpDelete("addresses/{id:guid}")]
    public async Task<IActionResult> DeleteAddress(Guid id, CancellationToken ct)
    {
        await accountService.DeleteAddressAsync(CustomerId, id, ct);
        return NoContent();
    }
}

