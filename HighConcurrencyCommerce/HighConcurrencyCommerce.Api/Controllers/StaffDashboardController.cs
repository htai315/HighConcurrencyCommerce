using System.Security.Claims;
using HighConcurrencyCommerce.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/dashboard")]
public sealed class StaffDashboardController(IReportingService reporting) : ControllerBase
{
    [HttpGet("tasks")]
    public async Task<ActionResult<StaffTaskSummaryDto>> Tasks(CancellationToken ct) =>
        Ok(await reporting.GetStaffTasksAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct));
}
