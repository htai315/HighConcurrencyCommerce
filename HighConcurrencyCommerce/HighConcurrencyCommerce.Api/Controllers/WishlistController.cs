using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using HighConcurrencyCommerce.Application.Wishlist;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
public sealed class WishlistController(
    IWishlistService wishlist,
    IWishlistAlertService alerts) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/v1/wishlist")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<WishlistPageDto>> GetWishlist(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        CancellationToken ct = default) =>
        Ok(await wishlist.GetWishlistAsync(CustomerId, page, pageSize, ct));

    [HttpGet("api/v1/wishlist/summary")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<WishlistSummaryDto>> GetSummary(CancellationToken ct = default) =>
        Ok(await wishlist.GetSummaryAsync(CustomerId, ct));

    [HttpPut("api/v1/wishlist/items/{productId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> AddItem(Guid productId, CancellationToken ct = default)
    {
        await wishlist.AddItemAsync(CustomerId, productId, ct);
        return NoContent();
    }

    [HttpDelete("api/v1/wishlist/items/{productId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RemoveItem(Guid productId, CancellationToken ct = default)
    {
        await wishlist.RemoveItemAsync(CustomerId, productId, ct);
        return NoContent();
    }

    [HttpGet("api/v1/wishlist/items/{productId:guid}/alerts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<WishlistAlertSettingsDto>> GetAlertSettings(
        Guid productId,
        CancellationToken ct = default) =>
        Ok(await alerts.GetSettingsAsync(CustomerId, productId, ct));

    [HttpPut("api/v1/wishlist/items/{productId:guid}/alerts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<WishlistAlertSettingsDto>> UpdateAlertSettings(
        Guid productId,
        UpdateWishlistAlertSettingsRequest request,
        CancellationToken ct = default) =>
        Ok(await alerts.UpdateSettingsAsync(CustomerId, productId, request, ct));
}

