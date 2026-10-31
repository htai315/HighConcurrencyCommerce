using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class AuthTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A long test password 123!";
    private static string Email() => Guid.NewGuid().ToString("N") + "@example.invalid";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object body, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task Register(HttpClient client, string email)
    {
        using var response = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Test Customer", role = "ADMIN" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<JsonElement> Login(HttpClient client, string email, string password = Password)
    {
        using var response = await Send(client, "auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Registration_normalizes_email_and_never_accepts_role_from_client()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, "  " + email.ToUpperInvariant() + "  ");
        var user = await Login(client, email);
        Assert.Equal("CUSTOMER", user.GetProperty("role").GetString());
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.DoesNotContain("password", user.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT password_hash FROM store.user_accounts WHERE email=@email", db);
        query.Parameters.AddWithValue("email", email);
        var hash = Assert.IsType<string>(await query.ExecuteScalarAsync());
        Assert.NotEqual(Password, hash);
        Assert.DoesNotContain(Password, hash);
    }

    [Fact]
    public async Task Duplicate_registration_is_generic_and_case_insensitive_even_when_racing()
    {
        var email = Email();
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        await Task.WhenAll(Register(first, email), Register(second, email.ToUpperInvariant()));
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT count(*) FROM store.user_accounts WHERE lower(email)=@email", db);
        query.Parameters.AddWithValue("email", email);
        Assert.Equal(1L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Session_cookie_is_secure_hash_only_and_logout_blocks_replay()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, email);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-hcc-session="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        var pair = cookie.Split(';')[0];
        var token = pair.Split('=', 2)[1];
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT token_hash FROM store.auth_sessions s JOIN store.user_accounts u ON s.user_id=u.id WHERE u.email=@email", db);
        query.Parameters.AddWithValue("email", email);
        var hash = Assert.IsType<string>(await query.ExecuteScalarAsync());
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(token, hash);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        using var logout = await Send(client, "auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var replay = fixture.CreateClient();
        replay.DefaultRequestHeaders.Add("Cookie", pair);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Mutations_require_matching_antiforgery_cookie_and_header()
    {
        using var client = fixture.CreateClient();
        using var other = fixture.CreateClient();
        var body = new { email = Email(), password = Password, confirmPassword = Password, fullName = "Customer" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register", body)).StatusCode);
        var foreignToken = await Csrf(other);
        await Csrf(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", foreignToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Missing_invalid_and_expired_sessions_are_401_without_redirect()
    {
        using var client = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        var email = Email();
        await Register(client, email);
        var user = await Login(client, email);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var expire = new NpgsqlCommand("UPDATE store.auth_sessions SET expires_at=now()-interval '1 second' WHERE user_id=@id", db);
        expire.Parameters.AddWithValue("id", user.GetProperty("id").GetGuid());
        await expire.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_missing_account_and_locked_account_share_error()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, email);
        using var wrong = await Send(client, "auth/login", new { email, password = "incorrect" });
        using var missing = await Send(client, "auth/login", new { email = Email(), password = Password });
        await using var db = await fixture.OpenDatabaseAsync();
        await using var change = new NpgsqlCommand("UPDATE store.user_accounts SET account_status='LOCKED' WHERE email=@email", db);
        change.Parameters.AddWithValue("email", email);
        await change.ExecuteNonQueryAsync();
        using var locked = await Send(client, "auth/login", new { email, password = Password });
        foreach (var response in new[] { wrong, missing, locked })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_CREDENTIALS", error.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("UPDATE store.user_accounts SET account_status='LOCKED' WHERE id=@id; UPDATE store.user_accounts SET account_status='ACTIVE' WHERE id=@id")]
    [InlineData("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') WHERE id=@id")]
    public async Task Account_security_changes_revoke_existing_sessions(string sql)
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, email);
        var user = await Login(client, email);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var change = new NpgsqlCommand(sql, db);
        change.Parameters.AddWithValue("id", user.GetProperty("id").GetGuid());
        await change.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_lock_another_account()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, email);
        await Login(client, email);
        using var response = await Send(client, "admin/accounts/00000000-0000-0000-0000-000000000002/status", new { status = "LOCKED" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Password_change_requires_current_password_and_revokes_all_sessions()
    {
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        var email = Email();
        await Register(first, email);
        await Login(first, email);
        await Login(second, email);
        using var wrong = await Send(first, "auth/password", new { currentPassword = "wrong", newPassword = "New test password 123!" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var changed = await Send(first, "auth/password", new { currentPassword = Password, newPassword = "New test password 123!" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        foreach (var client in new[] { first, second }) Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        await Login(first, email, "New test password 123!");
    }

    [Fact]
    public async Task Admin_can_lock_and_unlock_customer_with_audit_without_reviving_old_session()
    {
        using var admin = fixture.CreateClient();
        using var customer = fixture.CreateClient();
        var adminEmail = Email();
        var customerEmail = Email();
        await Register(admin, adminEmail);
        await Register(customer, customerEmail);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var promote = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') WHERE email=@email", db);
        promote.Parameters.AddWithValue("email", adminEmail);
        await promote.ExecuteNonQueryAsync();
        var adminUser = await Login(admin, adminEmail);
        var user = await Login(customer, customerEmail);
        var id = user.GetProperty("id").GetGuid();
        using var self = await Send(admin, $"admin/accounts/{adminUser.GetProperty("id").GetGuid()}/status", new { status = "LOCKED" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        foreach (var status in new[] { "LOCKED", "ACTIVE" })
        {
            using var response = await Send(admin, $"admin/accounts/{id}/status", new { status }, HttpMethod.Patch);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/v1/auth/me")).StatusCode);
        }
        await Login(customer, customerEmail);
        await using var audit = new NpgsqlCommand("SELECT count(*) FROM store.audit_logs WHERE entity_id=@id AND action='ACCOUNT_STATUS_CHANGED'", db);
        audit.Parameters.AddWithValue("id", id);
        Assert.Equal(2L, await audit.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("auth/login", 10)]
    [InlineData("auth/register", 5)]
    public async Task Auth_rate_limits_reject_excess_requests(string path, int allowed)
    {
        await using var application = fixture.CreateRateLimitedApplication();
        using var client = application.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        for (var i = 0; i < allowed; i++)
        {
            using var response = await Send(client, path, new { email = Email(), password = Password, confirmPassword = Password, fullName = "Rate test" });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        using var blocked = await Send(client, path, new { email = Email(), password = Password, confirmPassword = Password, fullName = "Rate test" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("bad-email", "Long enough password", "Name")]
    [InlineData("valid@example.invalid", "short", "Name")]
    [InlineData("valid@example.invalid", "Long enough password", "   ")]
    public async Task Registration_rejects_invalid_fields(string email, string password, string fullName)
    {
        using var client = fixture.CreateClient();
        using var response = await Send(client, "auth/register", new { email, password, confirmPassword = password, fullName });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_rotates_cookie_and_invalidates_previous_browser_token()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, email);
        using var first = await Send(client, "auth/login", new { email, password = Password });
        var oldCookie = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-hcc-session=")).Split(';')[0];
        await Login(client, email);
        using var replay = fixture.CreateClient();
        replay.DefaultRequestHeaders.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_only_revokes_current_device_and_requires_csrf()
    {
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        var email = Email();
        await Register(first, email);
        await Login(first, email);
        await Login(second, email);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsJsonAsync("/api/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/auth/me")).StatusCode);
        using var logout = await Send(first, "auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_lasts_seven_days_and_me_does_not_extend_expiry()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await Register(client, email);
        var user = await Login(client, email);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT created_at,expires_at FROM store.auth_sessions WHERE user_id=@id", db);
        query.Parameters.AddWithValue("id", user.GetProperty("id").GetGuid());
        DateTime expiry;
        await using (var reader = await query.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            expiry = reader.GetDateTime(1);
            Assert.Equal(TimeSpan.FromDays(7), expiry - reader.GetDateTime(0));
        }
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True(me.Headers.CacheControl?.NoStore);
        Assert.False(me.Headers.Contains("Set-Cookie"));
        await using var after = await query.ExecuteReaderAsync();
        Assert.True(await after.ReadAsync());
        Assert.Equal(expiry, after.GetDateTime(1));
    }

    [Theory]
    [InlineData("badminton123@", "badminton123@")]
    [InlineData("BADMINTON123@", "BADMINTON123@")]
    [InlineData("BadmintonABC@", "BadmintonABC@")]
    [InlineData("Badminton1234", "Badminton1234")]
    [InlineData("Badminton123@", "Different123@")]
    [InlineData("Badminton123@", "")]
    public async Task Registration_requires_complexity_and_matching_confirmation(string password, string confirmPassword)
    {
        using var client = fixture.CreateClient();
        using var response = await Send(client, "auth/register", new { email = Email(), password, confirmPassword, fullName = "Player" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
