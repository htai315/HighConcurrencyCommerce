using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Categories;

public sealed record AdminCategoryDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsVisible,
    int ProductCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class CreateCategoryRequest
{
    [Required, StringLength(180, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(220, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$", ErrorMessage = "Slug chỉ gồm chữ thường không dấu, số và dấu gạch ngang.")]
    public string Slug { get; init; } = string.Empty;

    public bool IsVisible { get; init; } = true;
}

public sealed class UpdateCategoryRequest
{
    [Required, StringLength(180, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(220, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$", ErrorMessage = "Slug chỉ gồm chữ thường không dấu, số và dấu gạch ngang.")]
    public string Slug { get; init; } = string.Empty;

    public bool IsVisible { get; init; }
}

public interface ICategoryManagementService
{
    Task<IReadOnlyList<AdminCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<AdminCategoryDto> CreateAsync(Guid adminId, CreateCategoryRequest request, string correlationId, CancellationToken ct = default);
    Task<AdminCategoryDto> UpdateAsync(Guid adminId, Guid categoryId, UpdateCategoryRequest request, string correlationId, CancellationToken ct = default);
    Task DeleteAsync(Guid adminId, Guid categoryId, string correlationId, CancellationToken ct = default);
}

public sealed class CategoryManagementFailure(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
