using System.Security.Claims;
using HighConcurrencyCommerce.Application.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/inventory")]
public sealed class StaffInventoryController(IInventoryService inventoryService) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkuInventorySummaryDto>>> GetInventory(CancellationToken ct) =>
        Ok(await inventoryService.GetInventorySummariesAsync(ct));

    [HttpGet("{skuId:guid}/movements")]
    public async Task<ActionResult<IReadOnlyList<InventoryMovementDto>>> GetMovements(Guid skuId, CancellationToken ct) =>
        Ok(await inventoryService.GetMovementsBySkuAsync(skuId, ct));

    [HttpPost("stock-in")]
    public async Task<ActionResult<StockInResultDto>> StockIn(StockInRequest request, CancellationToken ct)
    {
        var result = await inventoryService.StockInAsync(StaffId, request, ct);
        return Ok(result);
    }

    [HttpPost("adjustment-requests")]
    public async Task<ActionResult<InventoryAdjustmentDto>> CreateAdjustmentRequest(
        CreateInventoryAdjustmentRequest request,
        CancellationToken ct)
    {
        var result = await inventoryService.CreateAdjustmentRequestAsync(StaffId, request, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }
}

