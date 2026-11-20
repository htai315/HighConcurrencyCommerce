using System.Security.Cryptography;
using System.Text;
using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Infrastructure.CarrierTracking;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CarrierLabelStorageTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hcc-carrier-label-storage-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_open_and_delete_valid_pdf_preserves_hash_and_content()
    {
        var storage = CreateStorage();
        var consignmentId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\ncarrier label");

        var stored = await storage.StoreAsync(
            consignmentId,
            new MemoryStream(bytes),
            "../../unsafe-name.pdf",
            "application/pdf",
            bytes.Length);

        Assert.Matches($"^carrier-labels/{consignmentId:N}/[0-9a-f]{{32}}\\.pdf$", stored.StorageKey);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.Equal(bytes.Length, stored.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), stored.Sha256);

        var opened = await storage.OpenReadAsync(stored.StorageKey);
        Assert.NotNull(opened);
        await using (opened!.Content)
        {
            using var copy = new MemoryStream();
            await opened.Content.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        await storage.DeleteAsync(stored.StorageKey);
        Assert.Null(await storage.OpenReadAsync(stored.StorageKey));
    }

    [Fact]
    public async Task Store_rejects_unsupported_content_and_declared_type_mismatch()
    {
        var storage = CreateStorage();
        var unsupported = await Assert.ThrowsAsync<CarrierTrackingFailure>(() => storage.StoreAsync(
            Guid.NewGuid(),
            new MemoryStream("not-a-label"u8.ToArray()),
            "label.pdf",
            "application/pdf",
            11));
        Assert.Equal("LABEL_MEDIA_TYPE_UNSUPPORTED", unsupported.Code);
        Assert.Equal(415, unsupported.Status);

        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
        var mismatch = await Assert.ThrowsAsync<CarrierTrackingFailure>(() => storage.StoreAsync(
            Guid.NewGuid(),
            new MemoryStream(png),
            "label.pdf",
            "application/pdf",
            png.Length));
        Assert.Equal("LABEL_TYPE_MISMATCH", mismatch.Code);
        Assert.Equal(415, mismatch.Status);
    }

    [Fact]
    public async Task Store_enforces_streamed_size_and_storage_keys_cannot_traverse_root()
    {
        var storage = CreateStorage(maxFileBytes: 1024);
        var oversizedBytes = Encoding.ASCII.GetBytes("%PDF-" + new string('x', 1024));

        var oversized = await Assert.ThrowsAsync<CarrierTrackingFailure>(() => storage.StoreAsync(
            Guid.NewGuid(),
            new MemoryStream(oversizedBytes),
            "label.pdf",
            "application/pdf",
            length: 1));
        Assert.Equal("LABEL_TOO_LARGE", oversized.Code);
        Assert.Equal(413, oversized.Status);

        Assert.Null(await storage.OpenReadAsync("carrier-labels/../secret.pdf"));
        await storage.DeleteAsync("carrier-labels/../secret.pdf");
    }

    public void Dispose()
    {
        var resolvedRoot = Path.GetFullPath(rootPath);
        var safeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hcc-carrier-label-storage-tests"))
            + Path.DirectorySeparatorChar;
        if (resolvedRoot.StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedRoot))
            Directory.Delete(resolvedRoot, recursive: true);
    }

    private LocalCarrierLabelStorage CreateStorage(long maxFileBytes = 5L * 1024 * 1024) => new(
        Options.Create(new CarrierLabelOptions
        {
            RootPath = rootPath,
            MaxFileBytes = maxFileBytes
        }),
        new TestHostEnvironment { ContentRootPath = rootPath });

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "HighConcurrencyCommerce.IntegrationTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
