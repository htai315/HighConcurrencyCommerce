using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class BrandProductSpecificationTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";
    private async Task<HttpClient> Role(string role)
    {
        var client = fixture.CreateClient(); var email = $"brand_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = role + " Brand" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key=@role),email_verified_at=now() WHERE lower(email)=lower(@email)", db);
        cmd.Parameters.AddWithValue("role", role); cmd.Parameters.AddWithValue("email", email); await cmd.ExecuteNonQueryAsync();
        using var login = await Send(client, "auth/login", new { email, password = Password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
    private static async Task<string> Csrf(HttpClient client) { using var response = await client.GetAsync("/api/v1/auth/csrf"); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!; }
    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null)
    { using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path); if (body is not null) request.Content = JsonContent.Create(body); if ((method ?? HttpMethod.Post) != HttpMethod.Get) request.Headers.Add("X-CSRF-TOKEN", await Csrf(client)); return await client.SendAsync(request); }

    [Fact]
    public async Task Migration014_is_idempotent_and_mappings_are_queryable()
    {
        await using var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream("BrandProductSpecificationsSchema.sql")!;
        using var reader = new StreamReader(stream); var sql = await reader.ReadToEndAsync();
        await using var db = await fixture.OpenDatabaseAsync();
        for (var i = 0; i < 2; i++) { await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
        await using var verify = new NpgsqlCommand("SELECT to_regclass('store.brands') IS NOT NULL AND to_regclass('store.product_specifications') IS NOT NULL", db);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Brand_routes_enforce_roles()
    {
        using var guest = fixture.CreateClient(); using var customer = await Role("CUSTOMER"); using var staff = await Role("STAFF"); using var admin = await Role("ADMIN");
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/admin/brands")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/brands")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/admin/brands")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/brands")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/staff/brands")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_duplicate_brand_returns_created_and_conflict_and_in_use_brand_cannot_delete()
    {
        using var admin = await Role("ADMIN"); var slug = $"yonex-{Guid.NewGuid():N}";
        var responses = await Task.WhenAll(
            Send(admin, "admin/brands", new { name = "Yonex A", slug, country = "Japan", websiteUrl = "https://www.yonex.com", isActive = true }),
            Send(admin, "admin/brands", new { name = "Yonex B", slug, country = "Japan", websiteUrl = "https://www.yonex.com", isActive = true }));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created)); Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        var created = await responses.Single(x => x.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<JsonElement>(); var id = created.GetProperty("id").GetGuid();
        foreach (var response in responses) response.Dispose();
        await using var db = await fixture.OpenDatabaseAsync();
        await using (var insert = new NpgsqlCommand("INSERT INTO store.products(id,title,brand_id,publication_status,created_by) SELECT gen_random_uuid(),'Brand guard',@brand,'DRAFT',id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') LIMIT 1", db)) { insert.Parameters.AddWithValue("brand", id); await insert.ExecuteNonQueryAsync(); }
        using var deleted = await Send(admin, $"admin/brands/{id}", method: HttpMethod.Delete); Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
    }

    [Fact]
    public async Task Draft_brand_and_specifications_flow_to_public_catalog_and_freeze_after_publish()
    {
        using var admin = await Role("ADMIN"); using var staff = await Role("STAFF");
        using var brandResponse = await Send(admin, "admin/brands", new { name = "Victor", slug = $"victor-{Guid.NewGuid():N}", country = "Taiwan", websiteUrl = "https://www.victorsport.com", isActive = true });
        var brandId = (await brandResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var draftResponse = await Send(staff, "staff/products", new { title = "Vợt Victor Test", description = "Thông số public", brandId });
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode); var draft = await draftResponse.Content.ReadFromJsonAsync<JsonElement>(); var productId = draft.GetProperty("id").GetGuid();
        using var skuResponse = await Send(staff, $"staff/products/{productId}/skus", new { skuCode = $"VIC-{Guid.NewGuid():N}", optionLabel = "4U G5" });
        var skuId = (await skuResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var spec = await Send(staff, $"staff/products/{productId}/specifications/trong_luong", new { displayName = "Trọng lượng", value = "4U", displayOrder = 1 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        using var price = await Send(admin, $"admin/skus/{skuId}/price", new { price = 2500000 }, HttpMethod.Patch); Assert.Equal(HttpStatusCode.OK, price.StatusCode);
        using var publish = await Send(admin, $"admin/products/{productId}/publish", new { }); Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var publicResponse = await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId}"); var detail = await publicResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(brandId, detail.GetProperty("brand").GetProperty("id").GetGuid()); Assert.Equal("4U", detail.GetProperty("specifications")[0].GetProperty("value").GetString());
        using var mutatePublished = await Send(staff, $"staff/products/{productId}/specifications/trong_luong", new { displayName = "Trọng lượng", value = "3U", displayOrder = 1 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.BadRequest, mutatePublished.StatusCode);
    }
}
