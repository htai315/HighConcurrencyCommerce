using System.Security.Claims;
using HighConcurrencyCommerce.Application.FlashSale;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/flash-sales")]
public sealed class AdminFlashSaleController(IFlashSaleService flashSaleService) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FlashSaleCampaignSummaryDto>>> GetAll(CancellationToken ct) =>
        Ok(await flashSaleService.GetAdminCampaignsAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FlashSaleCampaignDetailDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await flashSaleService.GetAdminCampaignByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<FlashSaleCampaignDetailDto>> Create(
        CreateFlashSaleCampaignRequest request,
        CancellationToken ct)
    {
        var created = await flashSaleService.CreateCampaignAsync(AdminId, request, HttpContext.TraceIdentifier, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<FlashSaleCampaignDetailDto>> Update(
        Guid id,
        UpdateFlashSaleCampaignRequest request,
        CancellationToken ct) =>
        Ok(await flashSaleService.UpdateCampaignAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/offers")]
    public async Task<ActionResult<FlashSaleOfferDto>> AddOffer(
        Guid id,
        CreateFlashSaleOfferRequest request,
        CancellationToken ct) =>
        Created($"/api/v1/admin/flash-sales/{id}", await flashSaleService.AddOfferAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpDelete("{campaignId:guid}/offers/{offerId:guid}")]
    public async Task<IActionResult> DeleteOffer(
        Guid campaignId,
        Guid offerId,
        CancellationToken ct)
    {
        await flashSaleService.DeleteOfferAsync(AdminId, campaignId, offerId, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<FlashSaleCampaignDetailDto>> Publish(
        Guid id,
        CancellationToken ct) =>
        Ok(await flashSaleService.PublishCampaignAsync(AdminId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<FlashSaleCampaignDetailDto>> Cancel(
        Guid id,
        [FromBody] CancelFlashSaleCampaignRequest? request,
        CancellationToken ct) =>
        Ok(await flashSaleService.CancelCampaignAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpGet("{id:guid}/performance")]
    public async Task<ActionResult<FlashSalePerformanceDto>> GetPerformance(
        Guid id,
        CancellationToken ct) =>
        Ok(await flashSaleService.GetCampaignPerformanceAsync(id, ct));
}

