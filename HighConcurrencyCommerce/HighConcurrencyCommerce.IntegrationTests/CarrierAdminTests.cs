using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CarrierAdminTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";

    [Fact]
    public async Task Admin_carrier_routes_enforce_RBAC()
    {
        using var guest = fixture.CreateClient();
        using var customer = await CreateRoleClient("CUSTOMER");
        using var staff = await CreateRoleClient("STAFF");
        using var admin = await CreateRoleClient("ADMIN");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/v1/admin/carriers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync("/api/v1/admin/carriers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staff.GetAsync("/api/v1/admin/carriers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync("/api/v1/admin/carriers")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_manage_carrier_lifecycle_search_and_audit()
    {
        using var admin = await CreateRoleClient("ADMIN");
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var code = "car_" + suffix;
        using var createdResponse = await Send(admin, "admin/carriers", new
        {
            code,
            name = "  Hãng vận chuyển T49  ",
            hotline = " 19001009 ",
            trackingUrlTemplate = "https://tracking.example.com/parcel/{trackingNumber}"
        });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(code.ToUpperInvariant(), created.GetProperty("code").GetString());
        Assert.Equal("Hãng vận chuyển T49", created.GetProperty("name").GetString());
        Assert.Equal("INACTIVE", created.GetProperty("carrierStatus").GetString());
        Assert.Equal(1, created.GetProperty("revision").GetInt64());

        using var updatedResponse = await Send(admin, $"admin/carriers/{id}", new
        {
            name = "Hãng T49 cập nhật",
            hotline = "18001234",
            trackingUrlTemplate = "https://tracking.example.com/v2/{trackingNumber}",
            expectedRevision = 1L
        }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await updatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, updated.GetProperty("revision").GetInt64());

        using var activatedResponse = await Send(admin, $"admin/carriers/{id}/activate",
            new { expectedRevision = 2L });
        Assert.Equal(HttpStatusCode.OK, activatedResponse.StatusCode);
        var activated = await activatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE", activated.GetProperty("carrierStatus").GetString());
        Assert.Equal(3, activated.GetProperty("revision").GetInt64());

        using var idempotentResponse = await Send(admin, $"admin/carriers/{id}/activate",
            new { expectedRevision = 3L });
        Assert.Equal(HttpStatusCode.OK, idempotentResponse.StatusCode);
        Assert.Equal(3, (await idempotentResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("revision").GetInt64());

        using var listResponse = await admin.GetAsync(
            $"/api/v1/admin/carriers?q={Uri.EscapeDataString(suffix.ToLowerInvariant())}&status=active");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = Assert.Single((await listResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetGuid());
        Assert.Equal(0, listed.GetProperty("consignmentCount").GetInt32());

        using var deactivatedResponse = await Send(admin, $"admin/carriers/{id}/deactivate",
            new { expectedRevision = 3L });
        Assert.Equal(HttpStatusCode.OK, deactivatedResponse.StatusCode);
        var deactivated = await deactivatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INACTIVE", deactivated.GetProperty("carrierStatus").GetString());
        Assert.Equal(4, deactivated.GetProperty("revision").GetInt64());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var audit = new NpgsqlCommand("""
            SELECT count(*) FROM store.audit_logs
            WHERE entity_type='Carrier' AND entity_id=@id AND action IN (
                'CARRIER_CREATED','CARRIER_UPDATED','CARRIER_ACTIVATED','CARRIER_DEACTIVATED')
            """, db);
        audit.Parameters.AddWithValue("id", id);
        Assert.Equal(4L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Invalid_tracking_url_and_status_filter_return_structured_400()
    {
        using var admin = await CreateRoleClient("ADMIN");
        using var invalidUrl = await Send(admin, "admin/carriers", new
        {
            code = "URL_" + Guid.NewGuid().ToString("N")[..10],
            name = "Invalid URL carrier",
            trackingUrlTemplate = "http://tracking.example.com/no-placeholder"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUrl.StatusCode);
        Assert.Equal("CARRIER_TRACKING_URL_INVALID",
            (await invalidUrl.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var invalidStatus = await admin.GetAsync("/api/v1/admin/carriers?status=DELETED");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        Assert.Equal("VALIDATION_ERROR",
            (await invalidStatus.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Expected_revision_prevents_carrier_lost_updates_under_concurrency()
    {
        using var creator = await CreateRoleClient("ADMIN");
        using var firstAdmin = await CreateRoleClient("ADMIN");
        using var secondAdmin = await CreateRoleClient("ADMIN");
        var id = await CreateCarrier(creator, "RACE_" + Guid.NewGuid().ToString("N")[..10]);
        var first = new
        {
            name = "Carrier concurrent A", hotline = "19000001",
            trackingUrlTemplate = "https://a.example.com/{trackingNumber}", expectedRevision = 1L
        };
        var second = new
        {
            name = "Carrier concurrent B", hotline = "19000002",
            trackingUrlTemplate = "https://b.example.com/{trackingNumber}", expectedRevision = 1L
        };

        var responses = await Task.WhenAll(
            Send(firstAdmin, $"admin/carriers/{id}", first, HttpMethod.Patch),
            Send(secondAdmin, $"admin/carriers/{id}", second, HttpMethod.Patch));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).OrderBy(x => x).ToArray());
            var stale = responses.Single(x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("CARRIER_STALE",
                (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_carrier_code_returns_created_and_conflict_never_500()
    {
        using var firstAdmin = await CreateRoleClient("ADMIN");
        using var secondAdmin = await CreateRoleClient("ADMIN");
        var code = "DUP_" + Guid.NewGuid().ToString("N")[..12];
        var body = new { code, name = "Duplicate carrier" };

        var responses = await Task.WhenAll(
            Send(firstAdmin, "admin/carriers", body),
            Send(secondAdmin, "admin/carriers", body));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
            var conflict = responses.Single(x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("CARRIER_CODE_CONFLICT",
                (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    private async Task<Guid> CreateCarrier(HttpClient admin, string code)
    {
        using var response = await Send(admin, "admin/carriers", new
        {
            code,
            name = "Carrier temporary",
            trackingUrlTemplate = "https://tracking.example.com/{trackingNumber}"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> CreateRoleClient(string role)
    {
        var client = fixture.CreateClient();
        var email = $"carrier_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new
        {
            email, password = Password, confirmPassword = Password, fullName = $"{role} Carrier Test"
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

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if ((method ?? HttpMethod.Post) != HttpMethod.Get)
            request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }
}
