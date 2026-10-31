using System.Security.Claims;
using HighConcurrencyCommerce.Application.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/inventory/adjustment-requests")]
public sealed class AdminInventoryController(IInventoryService inventoryService) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InventoryAdjustmentDto>>> GetAll(CancellationToken ct) =>
        Ok(await inventoryService.GetAdjustmentRequestsAsync(ct));

    [HttpPost("{id:guid}/decision")]
    public async Task<ActionResult<InventoryAdjustmentDecisionResultDto>> Decide(
        Guid id,
        InventoryAdjustmentDecisionRequest request,
        CancellationToken ct) =>
        Ok(await inventoryService.DecideAdjustmentRequestAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));
}
