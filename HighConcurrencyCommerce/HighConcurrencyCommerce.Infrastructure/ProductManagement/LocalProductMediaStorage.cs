using System.Text.RegularExpressions;
using HighConcurrencyCommerce.Application.ProductManagement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.ProductManagement;

public sealed partial class LocalProductMediaStorage : IProductMediaStorage
{
    private const string PublicPrefix = "/api/v1/media/";
    private readonly string rootPath;
    private readonly long maxFileBytes;

    public LocalProductMediaStorage(IOptions<ProductMediaOptions> options, IHostEnvironment environment)
    {
        maxFileBytes = Math.Clamp(options.Value.MaxFileBytes, 1024, 32L * 1024 * 1024);
        rootPath = Path.GetFullPath(Path.IsPathRooted(options.Value.RootPath)
            ? options.Value.RootPath
            : Path.Combine(environment.ContentRootPath, options.Value.RootPath));
        Directory.CreateDirectory(rootPath);
    }

    public async Task<StoredProductMedia> StoreAsync(Guid productId, Stream content, string originalFileName, string declaredContentType, long length, CancellationToken ct = default)
    {
        if (length <= 0) throw new ProductManagementFailure("MEDIA_EMPTY", "Tệp ảnh đang trống.");
        if (length > maxFileBytes) throw new ProductManagementFailure("MEDIA_TOO_LARGE", $"Ảnh vượt quá giới hạn {maxFileBytes / 1024 / 1024} MiB.");
        if (!content.CanRead) throw new ProductManagementFailure("MEDIA_INVALID", "Không thể đọc tệp ảnh.");

        var productDirectory = Path.Combine(rootPath, "products", productId.ToString("N"));
        Directory.CreateDirectory(productDirectory);
        var temporaryPath = Path.Combine(productDirectory, $".{Guid.NewGuid():N}.upload");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    total += read;
                    if (total > maxFileBytes) throw new ProductManagementFailure("MEDIA_TOO_LARGE", $"Ảnh vượt quá giới hạn {maxFileBytes / 1024 / 1024} MiB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0) throw new ProductManagementFailure("MEDIA_EMPTY", "Tệp ảnh đang trống.");
                length = total;
            }

            var detected = await DetectAsync(temporaryPath, ct);
            if (detected is null) throw new ProductManagementFailure("MEDIA_UNSUPPORTED", "Chỉ hỗ trợ ảnh JPEG, PNG hoặc WebP hợp lệ.");
            if (!MimeMatches(declaredContentType, detected.Value.ContentType))
                throw new ProductManagementFailure("MEDIA_TYPE_MISMATCH", "Định dạng nội dung không khớp với tệp ảnh thực tế.");

            var fileName = $"{Guid.NewGuid():N}.{detected.Value.Extension}";
            var finalPath = Path.Combine(productDirectory, fileName);
            File.Move(temporaryPath, finalPath);
            var key = $"products/{productId:N}/{fileName}";
            return new StoredProductMedia(key, PublicPrefix + key, detected.Value.ContentType, length);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public Task<OpenProductMedia?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        if (!TryResolve(storageKey, out var fullPath) || !File.Exists(fullPath)) return Task.FromResult<OpenProductMedia?>(null);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = extension switch { ".jpg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "application/octet-stream" };
        var info = new FileInfo(fullPath);
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<OpenProductMedia?>(new OpenProductMedia(stream, contentType, info.Length));
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        if (TryResolve(storageKey, out var fullPath) && File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public bool TryGetStorageKey(string publicPath, out string storageKey)
    {
        storageKey = string.Empty;
        if (!publicPath.StartsWith(PublicPrefix, StringComparison.Ordinal)) return false;
        var candidate = Uri.UnescapeDataString(publicPath[PublicPrefix.Length..]);
        if (!StorageKeyPattern().IsMatch(candidate)) return false;
        storageKey = candidate;
        return true;
    }

    private bool TryResolve(string key, out string fullPath)
    {
        fullPath = string.Empty;
        if (!StorageKeyPattern().IsMatch(key)) return false;
        var candidate = Path.GetFullPath(Path.Combine(rootPath, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        fullPath = candidate;
        return true;
    }

    private static async Task<(string ContentType, string Extension)?> DetectAsync(string path, CancellationToken ct)
    {
        var header = new byte[16];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var read = await stream.ReadAsync(header, ct);
        if (read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ("image/png", "png");
        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ("image/jpeg", "jpg");
        if (read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ("image/webp", "webp");
        return null;
    }

    private static bool MimeMatches(string declared, string detected) =>
        string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase)
        || detected == "image/jpeg" && string.Equals(declared, "image/jpg", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^products/[0-9a-f]{32}/[0-9a-f]{32}\\.(?:jpg|png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageKeyPattern();
}
