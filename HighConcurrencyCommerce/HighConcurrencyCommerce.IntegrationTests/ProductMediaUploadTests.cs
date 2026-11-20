using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductMediaUploadTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure media pass 123!@#";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task Staff_uploads_reorders_updates_and_deletes_managed_images()
    {
        using var staff = await Role("STAFF");
        var productId = await CreateDraft(staff, "Media lifecycle");

        using var first = await Upload(staff, productId, Png, "front.png", "image/png", "Mặt trước", 5);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstJson.GetProperty("id").GetGuid();
        var firstUrl = firstJson.GetProperty("fileKey").GetString()!;
        Assert.StartsWith("/api/v1/media/products/", firstUrl);

        using var publicImage = await fixture.CreateClient().GetAsync(firstUrl);
        Assert.Equal(HttpStatusCode.OK, publicImage.StatusCode);
        Assert.Equal("image/png", publicImage.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await publicImage.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", publicImage.Headers.CacheControl?.Extensions.Select(x => x.Name) ?? []);

        using var second = await Upload(staff, productId, Png, "back.png", "image/png", "Mặt sau", 6);
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondJson.GetProperty("id").GetGuid();

        using var reorder = await Send(staff, $"staff/products/{productId}/images/order", new { imageIds = new[] { secondId, firstId } }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        var ordered = await reorder.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(secondId, ordered[0].GetProperty("id").GetGuid());
        Assert.Equal(0, ordered[0].GetProperty("displayOrder").GetInt32());

        using var metadata = await Send(staff, $"staff/products/{productId}/images/{firstId}", new { altText = "Ảnh chính đã sửa", displayOrder = 9 }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal("Ảnh chính đã sửa", (await metadata.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("altText").GetString());

        using var delete = await Send(staff, $"staff/products/{productId}/images/{firstId}", method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var missing = await fixture.CreateClient().GetAsync(firstUrl);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Upload_rejects_spoofed_content_and_freezes_after_publish()
    {
        using var staff = await Role("STAFF");
        var productId = await CreateDraft(staff, "Media validation");
        using var spoofed = await Upload(staff, productId, Encoding.UTF8.GetBytes("not really a png"), "fake.png", "image/png", null, 0);
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
        Assert.Equal("MEDIA_UNSUPPORTED", (await spoofed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var publish = new NpgsqlCommand("UPDATE store.products SET publication_status='PUBLISHED',published_at=now() WHERE id=@id", db))
        { publish.Parameters.AddWithValue("id", productId); await publish.ExecuteNonQueryAsync(); }

        using var frozen = await Upload(staff, productId, Png, "late.png", "image/png", null, 0);
        Assert.Equal(HttpStatusCode.BadRequest, frozen.StatusCode);
        Assert.Equal("INVALID_STATE", (await frozen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Media_upload_enforces_rbac_and_public_path_rejects_traversal()
    {
        using var staff = await Role("STAFF");
        using var customer = await Role("CUSTOMER");
        var productId = await CreateDraft(staff, "Media RBAC");
        using var customerUpload = await Upload(customer, productId, Png, "forbidden.png", "image/png", null, 0);
        Assert.Equal(HttpStatusCode.Forbidden, customerUpload.StatusCode);
        using var guest = fixture.CreateClient();
        using var guestUpload = await Upload(guest, productId, Png, "guest.png", "image/png", null, 0);
        Assert.Equal(HttpStatusCode.Unauthorized, guestUpload.StatusCode);
        using var traversal = await guest.GetAsync("/api/v1/media/products/not-a-safe-key.png");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
    }

    private async Task<Guid> CreateDraft(HttpClient client, string title)
    {
        using var response = await Send(client, "staff/products", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> Role(string role)
    {
        var client = fixture.CreateClient();
        var email = $"media_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = $"{role} Media" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var update = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key=@role),email_verified_at=now() WHERE lower(email)=lower(@email)", db);
        update.Parameters.AddWithValue("role", role); update.Parameters.AddWithValue("email", email); await update.ExecuteNonQueryAsync();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid productId, byte[] bytes, string fileName, string contentType, string? altText, int order)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/staff/products/{productId}/images/upload");
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(file, "file", fileName);
        if (altText is not null) multipart.Add(new StringContent(altText), "altText");
        multipart.Add(new StringContent(order.ToString(System.Globalization.CultureInfo.InvariantCulture)), "displayOrder");
        request.Content = multipart;
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if ((method ?? HttpMethod.Post) != HttpMethod.Get) request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
    }
}
