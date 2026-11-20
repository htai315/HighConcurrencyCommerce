using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CategoryManagementTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";

    private async Task<HttpClient> CreateRoleClient(string role)
    {
        var client = fixture.CreateClient();
        var email = $"category_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new
        {
            email, password = Password, confirmPassword = Password, fullName = $"{role} Category Test"
        });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleUpdate = new NpgsqlCommand("""
            UPDATE store.user_accounts
            SET role_id=(SELECT id FROM store.roles WHERE role_key=@role), email_verified_at=now()
            WHERE lower(email)=lower(@email)
            """, db);
        roleUpdate.Parameters.AddWithValue("role", role);
        roleUpdate.Parameters.AddWithValue("email", email);
        await roleUpdate.ExecuteNonQueryAsync();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if ((method ?? HttpMethod.Post) != HttpMethod.Get)
            request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Categories_admin_routes_enforce_RBAC()
    {
        using var guest = fixture.CreateClient();
        using var customer = await CreateRoleClient("CUSTOMER");
        using var staff = await CreateRoleClient("STAFF");
        using var admin = await CreateRoleClient("ADMIN");
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/admin/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/admin/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/categories")).StatusCode);
    }

    [Fact]
    public async Task Category_lifecycle_updates_public_visibility_and_writes_audit()
    {
        using var admin = await CreateRoleClient("ADMIN");
        var slug = $"phu-kien-{Guid.NewGuid():N}";
        using var createdResponse = await Send(admin, "admin/categories", new { name = "Phụ kiện thi đấu", slug, isVisible = true });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        using var publicBefore = await fixture.CreateClient().GetAsync("/api/v1/catalog/categories");
        Assert.Contains((await publicBefore.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);

        using var updatedResponse = await Send(admin, $"admin/categories/{id}", new { name = "Phụ kiện tập luyện", slug = slug + "-moi", isVisible = false }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        using var publicAfter = await fixture.CreateClient().GetAsync("/api/v1/catalog/categories");
        Assert.DoesNotContain((await publicAfter.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);

        using var deleted = await Send(admin, $"admin/categories/{id}", method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var audit = new NpgsqlCommand("SELECT count(*) FROM store.audit_logs WHERE entity_id=@id AND action IN ('CATEGORY_CREATED','CATEGORY_UPDATED','CATEGORY_DELETED')", db);
        audit.Parameters.AddWithValue("id", id);
        Assert.Equal(3L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Category_with_products_cannot_be_deleted()
    {
        using var admin = await CreateRoleClient("ADMIN");
        var slug = $"category-used-{Guid.NewGuid():N}";
        using var createdResponse = await Send(admin, "admin/categories", new { name = "Danh mục đang dùng", slug, isVisible = true });
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        await using var db = await fixture.OpenDatabaseAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,category_id,publication_status,created_by)
            SELECT gen_random_uuid(),'Sản phẩm giữ danh mục',@category,'DRAFT',id
            FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') LIMIT 1
            """, db))
        {
            insert.Parameters.AddWithValue("category", id);
            await insert.ExecuteNonQueryAsync();
        }

        using var response = await Send(admin, $"admin/categories/{id}", method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CATEGORY_IN_USE", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_duplicate_slug_create_returns_one_created_and_one_conflict()
    {
        using var admin = await CreateRoleClient("ADMIN");
        var slug = $"concurrent-{Guid.NewGuid():N}";
        var responses = await Task.WhenAll(
            Send(admin, "admin/categories", new { name = "Danh mục A", slug, isVisible = true }),
            Send(admin, "admin/categories", new { name = "Danh mục B", slug, isVisible = true }));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.InternalServerError);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }
}
