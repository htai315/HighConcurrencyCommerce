using System.Security.Claims;
using HighConcurrencyCommerce.Application.Returns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/returns")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminReturnsController(IOrderReturnAdminService adminService) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<AdminOrderReturnPageDto>> GetReturns(
        [FromQuery] AdminOrderReturnQuery query,
        CancellationToken ct) =>
        Ok(await adminService.GetAdminReturnsAsync(query, ct));

    [HttpGet("{returnId:guid}")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> GetById(
        Guid returnId,
        CancellationToken ct) =>
        Ok(await adminService.GetAdminReturnByIdAsync(returnId, ct));

    [HttpPost("{returnId:guid}/approve")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> Approve(
        Guid returnId,
        [FromBody] ApproveOrderReturnRequest request,
        CancellationToken ct) =>
        Ok(await adminService.ApproveReturnAsync(AdminId, returnId, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{returnId:guid}/reject")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> Reject(
        Guid returnId,
        [FromBody] RejectOrderReturnRequest request,
        CancellationToken ct) =>
        Ok(await adminService.RejectReturnAsync(AdminId, returnId, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{returnId:guid}/refund")]
    public async Task<ActionResult<AdminOrderReturnDetailDto>> ProcessRefund(
        Guid returnId,
        [FromBody] ProcessRefundRequest request,
        CancellationToken ct) =>
        Ok(await adminService.ProcessRefundAsync(AdminId, returnId, request, HttpContext.TraceIdentifier, ct));
}

