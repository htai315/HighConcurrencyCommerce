using HighConcurrencyCommerce.Application.FlashSale;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/catalog/flash-sales")]
public sealed class PublicFlashSaleController(IFlashSaleService flashSaleService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PublicFlashSaleCampaignDto>>> GetActive(CancellationToken ct) =>
        Ok(await flashSaleService.GetPublicActiveCampaignsAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PublicFlashSaleCampaignDto>> GetById(Guid id, CancellationToken ct)
    {
        var campaign = await flashSaleService.GetPublicCampaignByIdAsync(id, ct);
        return campaign is not null ? Ok(campaign) : Problem(statusCode: 404, title: "Không tìm thấy chiến dịch Flash Sale.");
    }
}

