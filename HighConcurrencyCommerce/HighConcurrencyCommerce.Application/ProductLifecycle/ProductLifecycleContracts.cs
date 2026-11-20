using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.ProductLifecycle;

public sealed class SuspendProductRequest
{
    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; init; } = string.Empty;
}

public sealed class ArchiveProductRequest
{
    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record ProductLifecycleDto(
    Guid ProductId,
    string PublicationStatus,
    DateTime StatusChangedAt,
    Guid? StatusChangedBy,
    string? StatusReason);

public sealed class ProductLifecycleFailure(string code, string message, int httpStatus = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
}

public interface IProductLifecycleService
{
    Task<ProductLifecycleDto> SuspendAsync(Guid adminId, Guid productId, SuspendProductRequest request, string correlationId, CancellationToken ct = default);
    Task<ProductLifecycleDto> ResumeAsync(Guid adminId, Guid productId, string correlationId, CancellationToken ct = default);
    Task<ProductLifecycleDto> ArchiveAsync(Guid adminId, Guid productId, ArchiveProductRequest request, string correlationId, CancellationToken ct = default);
}
