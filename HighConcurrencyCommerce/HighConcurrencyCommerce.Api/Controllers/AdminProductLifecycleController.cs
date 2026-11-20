using System.Security.Claims;
using HighConcurrencyCommerce.Application.ProductLifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/products/{productId:guid}")]
public sealed class AdminProductLifecycleController(IProductLifecycleService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("suspend")]
    public async Task<ActionResult<ProductLifecycleDto>> Suspend(Guid productId, SuspendProductRequest request, CancellationToken ct) =>
        Ok(await service.SuspendAsync(AdminId, productId, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("resume")]
    public async Task<ActionResult<ProductLifecycleDto>> Resume(Guid productId, CancellationToken ct) =>
        Ok(await service.ResumeAsync(AdminId, productId, HttpContext.TraceIdentifier, ct));

    [HttpPost("archive")]
    public async Task<ActionResult<ProductLifecycleDto>> Archive(Guid productId, ArchiveProductRequest request, CancellationToken ct) =>
        Ok(await service.ArchiveAsync(AdminId, productId, request, HttpContext.TraceIdentifier, ct));
}
