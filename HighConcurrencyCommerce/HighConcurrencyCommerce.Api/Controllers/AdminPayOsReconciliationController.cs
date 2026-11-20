using System.Security.Claims;
using HighConcurrencyCommerce.Application.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/payos-reconciliation")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminPayOsReconciliationController(IPayOsReconciliationService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<PayOsReconciliationPageDto>> Get(
        [FromQuery] PayOsReconciliationQuery query, CancellationToken ct) => Ok(await service.GetAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PayOsReconciliationDetailDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await service.GetByIdAsync(id, ct));

    [HttpPost("{id:guid}/claim")]
    public async Task<ActionResult<PayOsReconciliationDetailDto>> Claim(Guid id, CancellationToken ct) =>
        Ok(await service.ClaimAsync(AdminId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/resolve")]
    public async Task<ActionResult<PayOsReconciliationDetailDto>> Resolve(
        Guid id, ResolvePayOsReconciliationRequest request, CancellationToken ct) =>
        Ok(await service.ResolveAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/manual-refund")]
    public async Task<ActionResult<PayOsReconciliationDetailDto>> Refund(
        Guid id, ManualRefundPayOsReconciliationRequest request, CancellationToken ct) =>
        Ok(await service.RefundAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));
}
