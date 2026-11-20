using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ShippingAdminTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";

    [Fact]
    public async Task Admin_shipping_routes_enforce_RBAC()
    {
        using var guest = fixture.CreateClient();
        using var customer = await CreateRoleClient("CUSTOMER");
        using var staff = await CreateRoleClient("STAFF");
        using var admin = await CreateRoleClient("ADMIN");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/v1/admin/shipping-methods")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync("/api/v1/admin/shipping-methods")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staff.GetAsync("/api/v1/admin/shipping-methods")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync("/api/v1/admin/shipping-methods")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_manage_method_default_and_normalized_province_rate_with_audit()
    {
        using var admin = await CreateRoleClient("ADMIN");
        var code = "SHIP_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var createdResponse = await Send(admin, "admin/shipping-methods", new
        {
            code, name = "Giao hàng kiểm thử", description = "Cấu hình T48",
            baseFee = 25000, freeShippingThreshold = 500000,
            estimatedMinDays = 2, estimatedMaxDays = 4, displayOrder = 5
        });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("INACTIVE", created.GetProperty("methodStatus").GetString());
        Assert.Equal(1, created.GetProperty("revision").GetInt64());

        using var activatedResponse = await Send(admin, $"admin/shipping-methods/{id}/activate",
            new { expectedRevision = 1L });
        Assert.Equal(HttpStatusCode.OK, activatedResponse.StatusCode);
        var activated = await activatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, activated.GetProperty("revision").GetInt64());

        using var defaultResponse = await Send(admin, $"admin/shipping-methods/{id}/make-default",
            new { expectedRevision = 2L });
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        var madeDefault = await defaultResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(madeDefault.GetProperty("isDefault").GetBoolean());
        Assert.Equal(3, madeDefault.GetProperty("revision").GetInt64());

        using var rateResponse = await Send(admin,
            $"admin/shipping-methods/{id}/province-rates/{Uri.EscapeDataString("Thành phố Hồ Chí Minh")}",
            new { surchargeFee = 7000, isActive = true, expectedRevision = 3L }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, rateResponse.StatusCode);
        var withRate = await rateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, withRate.GetProperty("revision").GetInt64());
        var rate = Assert.Single(withRate.GetProperty("provinceRates").EnumerateArray());
        Assert.Equal("HO CHI MINH", rate.GetProperty("provinceKey").GetString());
        Assert.Equal(7000m, rate.GetProperty("surchargeFee").GetDecimal());

        using var blockedDeactivate = await Send(admin, $"admin/shipping-methods/{id}/deactivate",
            new { expectedRevision = 4L });
        Assert.Equal(HttpStatusCode.Conflict, blockedDeactivate.StatusCode);
        Assert.Equal("SHIPPING_DEFAULT_REQUIRED",
            (await blockedDeactivate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var removedRateResponse = await Send(admin,
            $"admin/shipping-methods/{id}/province-rates/{Uri.EscapeDataString("TP. Hồ Chí Minh")}?expectedRevision=4",
            method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.OK, removedRateResponse.StatusCode);
        var withoutRate = await removedRateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, withoutRate.GetProperty("revision").GetInt64());
        Assert.Empty(withoutRate.GetProperty("provinceRates").EnumerateArray());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var audit = new NpgsqlCommand("""
            SELECT count(*) FROM store.audit_logs
            WHERE entity_id=@id AND action IN (
                'SHIPPING_METHOD_CREATED','SHIPPING_METHOD_ACTIVATED',
                'SHIPPING_METHOD_DEFAULT_CHANGED','SHIPPING_PROVINCE_RATE_UPSERTED',
                'SHIPPING_PROVINCE_RATE_REMOVED')
            """, db);
        audit.Parameters.AddWithValue("id", id);
        Assert.Equal(5L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Expected_revision_prevents_lost_updates_under_concurrency()
    {
        using var admin = await CreateRoleClient("ADMIN");
        var id = await CreateMethod(admin, "RACE_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant());
        var first = new
        {
            name = "Cập nhật đồng thời A", description = "A", baseFee = 10000,
            freeShippingThreshold = (decimal?)null, estimatedMinDays = 2,
            estimatedMaxDays = 3, displayOrder = 1, expectedRevision = 1L
        };
        var second = new
        {
            name = "Cập nhật đồng thời B", description = "B", baseFee = 20000,
            freeShippingThreshold = (decimal?)null, estimatedMinDays = 3,
            estimatedMaxDays = 5, displayOrder = 2, expectedRevision = 1L
        };

        var responses = await Task.WhenAll(
            Send(admin, $"admin/shipping-methods/{id}", first, HttpMethod.Patch),
            Send(admin, $"admin/shipping-methods/{id}", second, HttpMethod.Patch));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).OrderBy(x => x).ToArray());
            var stale = responses.Single(x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("SHIPPING_METHOD_STALE",
                (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_code_returns_created_and_conflict_never_500()
    {
        using var firstAdmin = await CreateRoleClient("ADMIN");
        using var secondAdmin = await CreateRoleClient("ADMIN");
        var code = "DUP_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var body = new
        {
            code, name = "Mã trùng", baseFee = 10000,
            estimatedMinDays = 2, estimatedMaxDays = 4, displayOrder = 1
        };

        var responses = await Task.WhenAll(
            Send(firstAdmin, "admin/shipping-methods", body),
            Send(secondAdmin, "admin/shipping-methods", body));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Concurrent_default_changes_are_serialized_and_leave_exactly_one_default()
    {
        using var firstAdmin = await CreateRoleClient("ADMIN");
        using var secondAdmin = await CreateRoleClient("ADMIN");
        var firstId = await CreateMethod(firstAdmin, "DEF_A_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        var secondId = await CreateMethod(secondAdmin, "DEF_B_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        using (var firstActive = await Send(firstAdmin, $"admin/shipping-methods/{firstId}/activate", new { expectedRevision = 1L }))
            Assert.Equal(HttpStatusCode.OK, firstActive.StatusCode);
        using (var secondActive = await Send(secondAdmin, $"admin/shipping-methods/{secondId}/activate", new { expectedRevision = 1L }))
            Assert.Equal(HttpStatusCode.OK, secondActive.StatusCode);

        var responses = await Task.WhenAll(
            Send(firstAdmin, $"admin/shipping-methods/{firstId}/make-default", new { expectedRevision = 2L }),
            Send(secondAdmin, $"admin/shipping-methods/{secondId}/make-default", new { expectedRevision = 2L }));
        try
        {
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
            Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        await using var db = await fixture.OpenDatabaseAsync();
        await using var check = new NpgsqlCommand("""
            SELECT count(*) FROM store.shipping_methods
            WHERE method_status='ACTIVE' AND is_default
            """, db);
        Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
    }

    private async Task<Guid> CreateMethod(HttpClient admin, string code)
    {
        using var response = await Send(admin, "admin/shipping-methods", new
        {
            code, name = "Phương thức tạm", baseFee = 10000,
            estimatedMinDays = 2, estimatedMaxDays = 4, displayOrder = 1
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> CreateRoleClient(string role)
    {
        var client = fixture.CreateClient();
        var email = $"shipping_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new
        {
            email, password = Password, confirmPassword = Password, fullName = $"{role} Shipping Test"
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
