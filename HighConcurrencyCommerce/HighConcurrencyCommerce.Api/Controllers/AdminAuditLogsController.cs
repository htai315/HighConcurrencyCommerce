using HighConcurrencyCommerce.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/audit-logs")]
public sealed class AdminAuditLogsController(IReportingService reporting) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AuditLogPageDto>> List(
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] Guid? actorId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        Ok(await reporting.GetAuditLogsAsync(action, entityType, actorId, from, to, page, pageSize, ct));
}
