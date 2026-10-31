using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace HighConcurrencyCommerce.Application.Auth;

public sealed class StrongPasswordAttribute : ValidationAttribute
{
    public StrongPasswordAttribute() => ErrorMessage = "Mật khẩu cần 12–128 ký tự, có chữ hoa, chữ thường, số và ký tự đặc biệt (ví dụ @).";
    public override bool IsValid(object? value) => value is string password && IsStrong(password);
    public static bool IsStrong(string password) => password.Length is >= 12 and <= 128
        && Regex.IsMatch(password, "[A-Z]") && Regex.IsMatch(password, "[a-z]")
        && Regex.IsMatch(password, "[0-9]") && Regex.IsMatch(password, @"[^\p{L}\p{N}\s]");
}
