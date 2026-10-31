using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Account;

public sealed record CustomerProfileDto(Guid Id, string Email, string FullName, string? Phone, string Role);

public sealed class UpdateProfileRequest
{
    [Required(ErrorMessage = "Họ và tên không được để trống.")]
    [StringLength(160, MinimumLength = 1, ErrorMessage = "Họ và tên từ 1 đến 160 ký tự.")]
    public string FullName { get; set; } = string.Empty;

    [RegularExpression(@"^(\+?[0-9]{8,15})?$", ErrorMessage = "Số điện thoại không đúng định dạng (8 đến 15 chữ số).")]
    [StringLength(16, ErrorMessage = "Số điện thoại tối đa 16 ký tự.")]
    public string? Phone { get; set; }
}

public sealed record AddressDto(
    Guid Id,
    string RecipientName,
    string Phone,
    string AddressLine,
    string? Ward,
    string Province,
    bool IsDefault,
    DateTime CreatedAt);

public sealed class CreateAddressRequest
{
    [Required(ErrorMessage = "Tên người nhận không được để trống.")]
    [StringLength(160, MinimumLength = 1, ErrorMessage = "Tên người nhận từ 1 đến 160 ký tự.")]
    public string RecipientName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Số điện thoại không được để trống.")]
    [RegularExpression(@"^\+?[0-9]{8,15}$", ErrorMessage = "Số điện thoại không đúng định dạng (8 đến 15 chữ số).")]
    [StringLength(16, MinimumLength = 8, ErrorMessage = "Số điện thoại từ 8 đến 16 ký tự.")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Địa chỉ chi tiết không được để trống.")]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Địa chỉ chi tiết tối đa 500 ký tự.")]
    public string AddressLine { get; set; } = string.Empty;

    [StringLength(160, ErrorMessage = "Phường/Xã tối đa 160 ký tự.")]
    public string? Ward { get; set; }

    [Required(ErrorMessage = "Tỉnh/Thành phố không được để trống.")]
    [StringLength(160, MinimumLength = 1, ErrorMessage = "Tỉnh/Thành phố tối đa 160 ký tự.")]
    public string Province { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}

public sealed class UpdateAddressRequest
{
    [Required(ErrorMessage = "Tên người nhận không được để trống.")]
    [StringLength(160, MinimumLength = 1, ErrorMessage = "Tên người nhận từ 1 đến 160 ký tự.")]
    public string RecipientName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Số điện thoại không được để trống.")]
    [RegularExpression(@"^\+?[0-9]{8,15}$", ErrorMessage = "Số điện thoại không đúng định dạng (8 đến 15 chữ số).")]
    [StringLength(16, MinimumLength = 8, ErrorMessage = "Số điện thoại từ 8 đến 16 ký tự.")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Địa chỉ chi tiết không được để trống.")]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Địa chỉ chi tiết tối đa 500 ký tự.")]
    public string AddressLine { get; set; } = string.Empty;

    [StringLength(160, ErrorMessage = "Phường/Xã tối đa 160 ký tự.")]
    public string? Ward { get; set; }

    [Required(ErrorMessage = "Tỉnh/Thành phố không được để trống.")]
    [StringLength(160, MinimumLength = 1, ErrorMessage = "Tỉnh/Thành phố tối đa 160 ký tự.")]
    public string Province { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}

public sealed class AccountFailure(string code, string message = "") : Exception(message)
{
    public string Code { get; } = code;
}

public interface IAccountService
{
    Task<CustomerProfileDto> GetProfileAsync(Guid customerId, CancellationToken ct = default);
    Task<CustomerProfileDto> UpdateProfileAsync(Guid customerId, UpdateProfileRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AddressDto>> GetAddressesAsync(Guid customerId, CancellationToken ct = default);
    Task<AddressDto> GetAddressAsync(Guid customerId, Guid addressId, CancellationToken ct = default);
    Task<AddressDto> CreateAddressAsync(Guid customerId, CreateAddressRequest request, CancellationToken ct = default);
    Task<AddressDto> UpdateAddressAsync(Guid customerId, Guid addressId, UpdateAddressRequest request, CancellationToken ct = default);
    Task DeleteAddressAsync(Guid customerId, Guid addressId, CancellationToken ct = default);
}
