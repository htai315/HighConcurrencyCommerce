using System.Security.Claims;
using HighConcurrencyCommerce.Application.Vouchers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/vouchers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminVouchersController(IVoucherAdminService voucherAdminService) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<AdminVoucherPageDto>> GetVouchers(
        [FromQuery] AdminVoucherQuery query,
        CancellationToken ct) =>
        Ok(await voucherAdminService.GetVouchersAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AdminVoucherDetailDto>> GetById(
        Guid id,
        CancellationToken ct) =>
        Ok(await voucherAdminService.GetVoucherByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<AdminVoucherDetailDto>> Create(
        [FromBody] CreateVoucherRequest request,
        CancellationToken ct)
    {
        var created = await voucherAdminService.CreateVoucherAsync(AdminId, request, HttpContext.TraceIdentifier, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<AdminVoucherDetailDto>> Update(
        Guid id,
        [FromBody] UpdateVoucherRequest request,
        CancellationToken ct) =>
        Ok(await voucherAdminService.UpdateVoucherAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<AdminVoucherDetailDto>> Activate(
        Guid id,
        CancellationToken ct) =>
        Ok(await voucherAdminService.ActivateVoucherAsync(AdminId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<AdminVoucherDetailDto>> Pause(
        Guid id,
        CancellationToken ct) =>
        Ok(await voucherAdminService.PauseVoucherAsync(AdminId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<AdminVoucherDetailDto>> Resume(
        Guid id,
        CancellationToken ct) =>
        Ok(await voucherAdminService.ResumeVoucherAsync(AdminId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<AdminVoucherDetailDto>> End(
        Guid id,
        CancellationToken ct) =>
        Ok(await voucherAdminService.EndVoucherAsync(AdminId, id, HttpContext.TraceIdentifier, ct));
}

