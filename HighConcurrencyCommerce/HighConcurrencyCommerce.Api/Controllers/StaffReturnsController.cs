using System.Security.Claims;
using HighConcurrencyCommerce.Application.Returns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffReturnsController(IOrderReturnStaffService staffService) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/v1/staff/returns/approved")]
    public async Task<ActionResult<StaffOrderReturnPageDto>> GetApprovedQueue(
        [FromQuery] StaffOrderReturnQuery query,
        CancellationToken ct) =>
        Ok(await staffService.GetApprovedReturnsQueueAsync(query, ct));

    [HttpGet("api/v1/staff/returns/{returnId:guid}")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> GetById(Guid returnId, CancellationToken ct) =>
        Ok(await staffService.GetReturnByIdAsync(returnId, ct));

    [HttpPost("api/v1/staff/returns/{returnId:guid}/receive")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> Receive(
        Guid returnId,
        [FromBody] StaffReceiveReturnRequest request,
        CancellationToken ct) =>
        Ok(await staffService.ReceiveReturnAsync(StaffId, returnId, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("api/v1/staff/returns/{returnId:guid}/inspect")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> Inspect(
        Guid returnId,
        [FromBody] StaffInspectReturnRequest request,
        CancellationToken ct) =>
        Ok(await staffService.InspectReturnAsync(StaffId, returnId, request, HttpContext.TraceIdentifier, ct));
}

