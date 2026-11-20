using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.AdminAccounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Route("api/v1/auth/staff-invitation")]
public sealed class StaffInvitationController(IAdminAccountService adminAccounts) : ControllerBase
{
    [HttpPost("accept")]
    [AllowAnonymous]
    [EnableRateLimiting("staff-invitation-accept")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Accept(AcceptStaffInvitationRequest request, CancellationToken ct)
    {
        await adminAccounts.AcceptStaffInvitationAsync(request, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
}
