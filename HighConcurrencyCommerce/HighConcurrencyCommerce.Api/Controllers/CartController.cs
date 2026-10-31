using System.Security.Claims;
using HighConcurrencyCommerce.Application.Cart;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/v1/cart")]
public sealed class CartController(ICartService cartService) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<CartDto>> Get(CancellationToken ct) => Ok(await cartService.GetAsync(CustomerId, ct));

    [HttpPut("items/{skuId:guid}")]
    public async Task<ActionResult<CartDto>> SetItem(Guid skuId, SetCartItemRequest request, CancellationToken ct) =>
        Ok(await cartService.SetItemAsync(CustomerId, skuId, request, ct));

    [HttpDelete("items/{skuId:guid}")]
    public async Task<ActionResult<CartDto>> RemoveItem(Guid skuId, CancellationToken ct) =>
        Ok(await cartService.RemoveItemAsync(CustomerId, skuId, ct));

    [HttpPost("merge")]
    public async Task<ActionResult<MergeCartResultDto>> Merge(MergeCartRequest request, CancellationToken ct) =>
        Ok(await cartService.MergeAsync(CustomerId, request, ct));

    [HttpPost("validate")]
    public async Task<ActionResult<CartValidationDto>> Validate(CancellationToken ct) =>
        Ok(await cartService.ValidateAsync(CustomerId, ct));
}
