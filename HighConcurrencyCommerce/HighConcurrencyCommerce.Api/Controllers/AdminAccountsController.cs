using System.Security.Claims;
using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.AdminAccounts;
using HighConcurrencyCommerce.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/accounts")]
public sealed class AdminAccountsController(IAdminAccountService accounts) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<AdminAccountPageDto>> GetAccounts([FromQuery] AdminAccountQuery query, CancellationToken ct)
    {
        var result = await accounts.GetAccountsAsync(query, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<AdminAccountDetailDto>> GetAccount(Guid id, CancellationToken ct)
    {
        var result = await accounts.GetAccountByIdAsync(id, ct);
        return Ok(result);
    }

    [HttpPost("/api/v1/admin/staff-invitations")]
    public async Task<ActionResult<AdminAccountDetailDto>> InviteStaff(InviteStaffRequest request, CancellationToken ct)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await accounts.InviteStaffAsync(actorId, request, HttpContext.TraceIdentifier, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpPost("{id:guid}/staff-invitation/resend")]
    public async Task<IActionResult> ResendStaffInvitation(Guid id, CancellationToken ct)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await accounts.ResendStaffInvitationAsync(actorId, id, HttpContext.TraceIdentifier, ct);
        return Accepted();
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, AccountStatusRequest request, CancellationToken ct)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await accounts.SetAccountStatusAsync(actorId, id, request.Status, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/sessions/revoke")]
    public async Task<IActionResult> RevokeSessions(Guid id, CancellationToken ct)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await accounts.RevokeAccountSessionsAsync(actorId, id, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
}

