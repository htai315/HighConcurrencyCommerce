using HighConcurrencyCommerce.Application.ProductManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController, AllowAnonymous, Route("api/v1/media")]
public sealed class ProductMediaController(IProductMediaStorage storage) : ControllerBase
{
    [HttpGet("{**storageKey}")]
    public async Task<IActionResult> Get(string storageKey, CancellationToken ct)
    {
        var media = await storage.OpenReadAsync(storageKey, ct);
        if (media is null) return NotFound();
        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        Response.Headers.ETag = $"\"{Path.GetFileNameWithoutExtension(storageKey)}\"";
        Response.Headers.Append("X-Content-Type-Options", "nosniff");
        return File(media.Content, media.ContentType, enableRangeProcessing: true);
    }
}
