using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Brands;

public sealed record BrandDto(Guid Id, string Name, string Slug, string? Country, string? WebsiteUrl, bool IsActive, int ProductCount, DateTime CreatedAt, DateTime UpdatedAt);

public sealed class BrandMutationRequest
{
    [Required, StringLength(160, MinimumLength = 2)] public string Name { get; init; } = string.Empty;
    [Required, StringLength(180, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")] public string Slug { get; init; } = string.Empty;
    [StringLength(100)] public string? Country { get; init; }
    [StringLength(500), Url] public string? WebsiteUrl { get; init; }
    public bool IsActive { get; init; } = true;
}

public interface IBrandManagementService
{
    Task<IReadOnlyList<BrandDto>> GetAllAsync(CancellationToken ct = default);
    Task<BrandDto> CreateAsync(Guid adminId, BrandMutationRequest request, string correlationId, CancellationToken ct = default);
    Task<BrandDto> UpdateAsync(Guid adminId, Guid id, BrandMutationRequest request, string correlationId, CancellationToken ct = default);
    Task DeleteAsync(Guid adminId, Guid id, string correlationId, CancellationToken ct = default);
}

public sealed class BrandManagementFailure(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
