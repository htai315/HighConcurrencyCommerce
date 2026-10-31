using HighConcurrencyCommerce.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/reports")]
public sealed class AdminReportsController(IReportingService reporting) : ControllerBase
{
    [HttpGet("revenue")]
    public async Task<ActionResult<RevenueReportDto>> Revenue(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await reporting.GetRevenueAsync(from, to, ct));

    [HttpGet("low-stock")]
    public async Task<ActionResult<LowStockReportDto>> LowStock(CancellationToken ct) =>
        Ok(await reporting.GetLowStockAsync(ct));
}
