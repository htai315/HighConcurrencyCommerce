using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HighConcurrencyCommerce.Application.CarrierTracking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.CarrierTracking;

public sealed partial class LocalCarrierLabelStorage : ICarrierLabelStorage
{
    private readonly string rootPath;
    private readonly long maxFileBytes;

    public LocalCarrierLabelStorage(IOptions<CarrierLabelOptions> options, IHostEnvironment environment)
    {
        maxFileBytes = Math.Clamp(options.Value.MaxFileBytes, 1024, 10L * 1024 * 1024);
        rootPath = Path.GetFullPath(Path.IsPathRooted(options.Value.RootPath)
            ? options.Value.RootPath
            : Path.Combine(environment.ContentRootPath, options.Value.RootPath));
        Directory.CreateDirectory(rootPath);
    }

    public async Task<StoredCarrierLabel> StoreAsync(
        Guid consignmentId,
        Stream content,
        string originalFileName,
        string declaredContentType,
        long length,
        CancellationToken ct = default)
    {
        _ = originalFileName;
        if (length <= 0) throw Failure("LABEL_EMPTY", "Tệp nhãn đang trống.");
        if (length > maxFileBytes) throw Failure("LABEL_TOO_LARGE", $"Nhãn vượt quá giới hạn {maxFileBytes / 1024 / 1024} MiB.", 413);
        if (!content.CanRead) throw Failure("LABEL_INVALID", "Không thể đọc tệp nhãn.");

        var directory = Path.Combine(rootPath, consignmentId.ToString("N"));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.upload");
        try
        {
            long total = 0;
            string sha256;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await content.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    total += read;
                    if (total > maxFileBytes) throw Failure("LABEL_TOO_LARGE", $"Nhãn vượt quá giới hạn {maxFileBytes / 1024 / 1024} MiB.", 413);
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0) throw Failure("LABEL_EMPTY", "Tệp nhãn đang trống.");
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            var detected = await DetectAsync(temporaryPath, ct)
                ?? throw Failure("LABEL_MEDIA_TYPE_UNSUPPORTED", "Chỉ hỗ trợ nhãn PDF, PNG hoặc JPEG hợp lệ.", 415);
            if (!MimeMatches(declaredContentType, detected.ContentType))
                throw Failure("LABEL_TYPE_MISMATCH", "Định dạng khai báo không khớp nội dung nhãn.", 415);

            var fileName = $"{Guid.NewGuid():N}.{detected.Extension}";
            var finalPath = Path.Combine(directory, fileName);
            File.Move(temporaryPath, finalPath);
            return new StoredCarrierLabel(
                $"carrier-labels/{consignmentId:N}/{fileName}",
                detected.ContentType,
                total,
                sha256);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public Task<OpenCarrierLabel?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        if (!TryResolve(storageKey, out var fullPath) || !File.Exists(fullPath))
            return Task.FromResult<OpenCarrierLabel?>(null);
        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            _ => "application/octet-stream"
        };
        var info = new FileInfo(fullPath);
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<OpenCarrierLabel?>(new OpenCarrierLabel(stream, contentType, info.Length));
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        if (TryResolve(storageKey, out var fullPath) && File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    private bool TryResolve(string storageKey, out string fullPath)
    {
        fullPath = string.Empty;
        if (!StorageKeyPattern().IsMatch(storageKey)) return false;
        var relative = storageKey["carrier-labels/".Length..];
        var candidate = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        fullPath = candidate;
        return true;
    }

    private static async Task<(string ContentType, string Extension)?> DetectAsync(string path, CancellationToken ct)
    {
        var header = new byte[16];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var read = await stream.ReadAsync(header, ct);
        if (read >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return ("application/pdf", "pdf");
        if (read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ("image/png", "png");
        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ("image/jpeg", "jpg");
        return null;
    }

    private static bool MimeMatches(string declared, string detected) =>
        string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase)
        || detected == "image/jpeg" && string.Equals(declared, "image/jpg", StringComparison.OrdinalIgnoreCase);

    private static CarrierTrackingFailure Failure(string code, string message, int status = 400) => new(code, message, status);

    [GeneratedRegex("^carrier-labels/[0-9a-f]{32}/[0-9a-f]{32}\\.(?:pdf|png|jpg)$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageKeyPattern();
}
