using System.Globalization;
using System.Text;

namespace HighConcurrencyCommerce.Application.Shipping;

public sealed class ProvinceKeyNormalizer : IProvinceKeyNormalizer
{
    private static readonly string[] AdministrativePrefixes = ["THANH PHO ", "TINH ", "TP "];

    public string Normalize(string province)
    {
        if (string.IsNullOrWhiteSpace(province))
            throw new ArgumentException("Tỉnh/thành không được để trống.", nameof(province));

        var decomposed = province.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            var normalized = character is 'Đ' or 'đ' ? 'D' : char.ToUpperInvariant(character);
            if (char.IsLetterOrDigit(normalized))
            {
                builder.Append(normalized);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        var key = builder.ToString().Trim();
        foreach (var prefix in AdministrativePrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                key = key[prefix.Length..].TrimStart();
                break;
            }
        }

        if (key.Length == 0)
            throw new ArgumentException("Tỉnh/thành không hợp lệ.", nameof(province));
        if (key.Length > 160)
            throw new ArgumentException("Khóa tỉnh/thành vượt quá 160 ký tự.", nameof(province));

        return key;
    }
}
