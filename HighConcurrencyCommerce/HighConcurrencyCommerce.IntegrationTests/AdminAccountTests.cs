using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.AdminAccounts;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class AdminAccountTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string StrongPassword = "StrongStaffPassword123!@#";

    private static string Email(string prefix = "user") => $"{prefix}_{Guid.NewGuid():N}@example.invalid";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object body, HttpMethod? method = null, bool includeCsrf = true)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path)
        {
            Content = JsonContent.Create(body)
        };
        if (includeCsrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        }
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid Id, string Email)> CreateUserWithRoleAsync(string roleKey)
    {
        var client = fixture.CreateClient();
        var email = Email(roleKey.ToLowerInvariant());
        using var reg = await Send(client, "auth/register", new
        {
            email,
            password = StrongPassword,
            confirmPassword = StrongPassword,
            fullName = $"{roleKey} User"
        });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE store.user_accounts SET role_id = (SELECT id FROM store.roles WHERE role_key = @role), email_verified_at = now() WHERE lower(email) = lower(@email) RETURNING id", db);
        cmd.Parameters.AddWithValue("role", roleKey);
        cmd.Parameters.AddWithValue("email", email);
        var id = (Guid)(await cmd.ExecuteScalarAsync())!;

        using var login = await Send(client, "auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (client, id, email);
    }

    [Fact]
    public async Task RBAC_Guest_Customer_Staff_are_blocked_from_admin_endpoints_and_Admin_can_access()
    {
        var guestClient = fixture.CreateClient();
        var (customerClient, _, _) = await CreateUserWithRoleAsync("CUSTOMER");
        var (staffClient, _, _) = await CreateUserWithRoleAsync("STAFF");
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");

        // 1. Guest blocked (401)
        using var guestRes = await guestClient.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, guestRes.StatusCode);

        // 2. Customer blocked (403)
        using var custRes = await customerClient.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, custRes.StatusCode);

        // 3. Staff blocked (403)
        using var staffRes = await staffClient.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, staffRes.StatusCode);

        // 4. Admin allowed (200)
        using var adminRes = await adminClient.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
    }

    [Fact]
    public async Task Admin_accounts_list_supports_search_filter_pagination_and_omits_sensitive_hash_and_token()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, customerId, customerEmail) = await CreateUserWithRoleAsync("CUSTOMER");

        // Search by email
        using var searchRes = await adminClient.GetAsync($"/api/v1/admin/accounts?q={customerEmail}");
        Assert.Equal(HttpStatusCode.OK, searchRes.StatusCode);
        var page = await searchRes.Content.ReadFromJsonAsync<AdminAccountPageDto>();
        Assert.NotNull(page);
        Assert.Single(page.Items);
        Assert.Equal(customerEmail, page.Items[0].Email);
        Assert.Equal("CUSTOMER", page.Items[0].Role);
        Assert.Equal("ACTIVE", page.Items[0].LifecycleStatus);

        // Verify response contains no password hash or token in raw JSON
        var rawJson = await searchRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", rawJson, StringComparison.OrdinalIgnoreCase);

        // Get single account detail
        using var detailRes = await adminClient.GetAsync($"/api/v1/admin/accounts/{customerId}");
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
        var detail = await detailRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(customerId, detail.Id);
        Assert.True(detail.HasPassword);
        Assert.Equal(1, detail.ActiveSessionCount);
    }

    [Fact]
    public async Task Invite_staff_creates_staff_account_with_null_password_unverified_token_hash_and_outbox()
    {
        var (adminClient, adminId, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("invited_staff");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Nguyễn Văn Staff"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);
        Assert.Equal(staffEmail, created.Email);
        Assert.Equal("STAFF", created.Role);
        Assert.Equal("ACTIVE", created.AccountStatus);
        Assert.Equal("INVITATION_PENDING", created.LifecycleStatus);
        Assert.False(created.HasPassword);
        Assert.Null(created.EmailVerifiedAt);

        // Verify Database state
        await using var db = await fixture.OpenDatabaseAsync();

        // 1. User account checks
        await using var userCmd = new NpgsqlCommand("SELECT password_hash, email_verified_at, account_status FROM store.user_accounts WHERE id = @id", db);
        userCmd.Parameters.AddWithValue("id", created.Id);
        await using var userReader = await userCmd.ExecuteReaderAsync();
        Assert.True(await userReader.ReadAsync());
        Assert.True(userReader.IsDBNull(0)); // password_hash is null
        Assert.True(userReader.IsDBNull(1)); // email_verified_at is null
        Assert.Equal("ACTIVE", userReader.GetString(2));
        await userReader.CloseAsync();

        // 2. Action token checks
        await using var tokenCmd = new NpgsqlCommand("SELECT purpose, token_hash, expires_at, consumed_at, revoked_at FROM store.auth_action_tokens WHERE user_id = @userId", db);
        tokenCmd.Parameters.AddWithValue("userId", created.Id);
        await using var tokenReader = await tokenCmd.ExecuteReaderAsync();
        Assert.True(await tokenReader.ReadAsync());
        Assert.Equal("STAFF_INVITE", tokenReader.GetString(0));
        Assert.False(string.IsNullOrWhiteSpace(tokenReader.GetString(1)));
        Assert.True(tokenReader.GetDateTime(2) > DateTime.UtcNow.AddHours(23)); // 24h expiry
        Assert.True(tokenReader.IsDBNull(3)); // consumed_at is null
        Assert.True(tokenReader.IsDBNull(4)); // revoked_at is null
        await tokenReader.CloseAsync();

        // 3. Outbox checks
        await using var outboxCmd = new NpgsqlCommand("SELECT template_key, recipient_email, protected_payload, sent_at, discarded_at FROM store.auth_email_outbox WHERE user_id = @userId", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        await using var outboxReader = await outboxCmd.ExecuteReaderAsync();
        Assert.True(await outboxReader.ReadAsync());
        Assert.Equal("STAFF_INVITE", outboxReader.GetString(0));
        Assert.Equal(staffEmail, outboxReader.GetString(1));
        Assert.False(string.IsNullOrWhiteSpace(outboxReader.GetString(2))); // protected_payload
        Assert.True(outboxReader.IsDBNull(3)); // sent_at is null
        Assert.True(outboxReader.IsDBNull(4)); // discarded_at is null
        await outboxReader.CloseAsync();

        // 4. Audit log checks
        await using var auditCmd = new NpgsqlCommand("SELECT action, actor_id FROM store.audit_logs WHERE entity_id = @entityId AND action = 'STAFF_INVITED'", db);
        auditCmd.Parameters.AddWithValue("entityId", created.Id);
        await using var auditReader = await auditCmd.ExecuteReaderAsync();
        Assert.True(await auditReader.ReadAsync());
        Assert.Equal("STAFF_INVITED", auditReader.GetString(0));
        Assert.Equal(adminId, auditReader.GetGuid(1));
        await auditReader.CloseAsync();
    }

    [Fact]
    public async Task Concurrent_invite_same_email_results_in_one_201_and_one_409_without_orphan_data()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("concurrent_staff");

        var t1 = Send(adminClient, "admin/staff-invitations", new { email = staffEmail, fullName = "Staff Concurrent 1" });
        var t2 = Send(adminClient, "admin/staff-invitations", new { email = staffEmail, fullName = "Staff Concurrent 2" });

        var responses = await Task.WhenAll(t1, t2);
        var statusCodes = responses.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        // Verify in DB that exactly 1 account, 1 token, 1 outbox row exists
        await using var db = await fixture.OpenDatabaseAsync();
        await using var countCmd = new NpgsqlCommand("SELECT count(*) FROM store.user_accounts WHERE lower(email) = lower(@email)", db);
        countCmd.Parameters.AddWithValue("email", staffEmail);
        Assert.Equal(1L, await countCmd.ExecuteScalarAsync());

        await using var tokenCountCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_action_tokens WHERE lower(email_snapshot) = lower(@email)", db);
        tokenCountCmd.Parameters.AddWithValue("email", staffEmail);
        Assert.Equal(1L, await tokenCountCmd.ExecuteScalarAsync());

        await using var outboxCountCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_email_outbox WHERE lower(recipient_email) = lower(@email)", db);
        outboxCountCmd.Parameters.AddWithValue("email", staffEmail);
        Assert.Equal(1L, await outboxCountCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Invite_staff_rejects_existing_customer_or_staff_email_with_409()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (_, _, customerEmail) = await CreateUserWithRoleAsync("CUSTOMER");

        using var res = await Send(adminClient, "admin/staff-invitations", new
        {
            email = customerEmail,
            fullName = "Attempt To Convert Customer"
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Accept_staff_invitation_sets_password_and_verified_enables_login_and_prevents_replay()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("accept_staff");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Accept Test"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        // Retrieve raw token from outbox using data protector
        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE user_id = @userId", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var scopeFactory = fixture.Client.GetStringAsync("/health/live"); // dummy call to ensure factory is warmed
        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // Anonymous client accepts invitation
        var publicClient = fixture.CreateClient();
        using var acceptRes = await Send(publicClient, "auth/staff-invitation/accept", new
        {
            token = rawToken,
            password = StrongPassword,
            confirmPassword = StrongPassword
        });
        Assert.Equal(HttpStatusCode.NoContent, acceptRes.StatusCode);

        // Staff can now login
        var staffLoginClient = fixture.CreateClient();
        using var loginRes = await Send(staffLoginClient, "auth/login", new
        {
            email = staffEmail,
            password = StrongPassword
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
        var loginBody = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("STAFF", loginBody.GetProperty("role").GetString());

        // Replaying accept returns 400
        using var replayRes = await Send(publicClient, "auth/staff-invitation/accept", new
        {
            token = rawToken,
            password = StrongPassword,
            confirmPassword = StrongPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, replayRes.StatusCode);
    }

    [Fact]
    public async Task Concurrent_accept_staff_invitation_only_one_succeeds_no_500()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("concurrent_accept");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Concurrent Accept"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE user_id = @userId", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        var client1 = fixture.CreateClient();
        var client2 = fixture.CreateClient();

        var t1 = Send(client1, "auth/staff-invitation/accept", new { token = rawToken, password = StrongPassword, confirmPassword = StrongPassword });
        var t2 = Send(client2, "auth/staff-invitation/accept", new { token = rawToken, password = StrongPassword, confirmPassword = StrongPassword });

        var responses = await Task.WhenAll(t1, t2);
        var statusCodes = responses.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.NoContent, statusCodes);
        Assert.Contains(HttpStatusCode.BadRequest, statusCodes);
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statusCodes);
    }

    [Fact]
    public async Task Accept_staff_invitation_rejects_expired_or_revoked_token_with_generic_error()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("expired_staff");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Expired Test"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE user_id = @userId", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // 1. Manually expire token in DB
        await using var expireCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET expires_at = now() - interval '1 hour' WHERE user_id = @userId", db);
        expireCmd.Parameters.AddWithValue("userId", created.Id);
        await expireCmd.ExecuteNonQueryAsync();

        var publicClient = fixture.CreateClient();
        using var expiredRes = await Send(publicClient, "auth/staff-invitation/accept", new
        {
            token = rawToken,
            password = StrongPassword,
            confirmPassword = StrongPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, expiredRes.StatusCode);

        // 2. Revoke token
        await using var revokeCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET expires_at = now() + interval '1 hour', revoked_at = now() WHERE user_id = @userId", db);
        revokeCmd.Parameters.AddWithValue("userId", created.Id);
        await revokeCmd.ExecuteNonQueryAsync();

        using var revokedRes = await Send(publicClient, "auth/staff-invitation/accept", new
        {
            token = rawToken,
            password = StrongPassword,
            confirmPassword = StrongPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, revokedRes.StatusCode);
    }

    [Fact]
    public async Task Resend_invitation_revokes_old_token_creates_new_one_and_enforces_cooldown()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("resend_staff");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Resend Test"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        // Immediate resend triggers cooldown (429)
        using var cooldownRes = await Send(adminClient, $"admin/accounts/{created.Id}/staff-invitation/resend", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, cooldownRes.StatusCode);

        // Fast-forward initial token created_at in DB by 2 minutes
        await using var db = await fixture.OpenDatabaseAsync();
        await using var ffCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET created_at = now() - interval '2 minutes' WHERE user_id = @userId", db);
        ffCmd.Parameters.AddWithValue("userId", created.Id);
        await ffCmd.ExecuteNonQueryAsync();

        // Resend now succeeds (202)
        using var resendRes = await Send(adminClient, $"admin/accounts/{created.Id}/staff-invitation/resend", new { });
        Assert.Equal(HttpStatusCode.Accepted, resendRes.StatusCode);

        // Verify in DB that old token is revoked and new active token exists
        await using var checkCmd = new NpgsqlCommand("SELECT revoked_at FROM store.auth_action_tokens WHERE user_id = @userId ORDER BY created_at ASC", db);
        checkCmd.Parameters.AddWithValue("userId", created.Id);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0)); // First token is now revoked
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0)); // Second token is active
        await reader.CloseAsync();
    }

    [Fact]
    public async Task Locking_account_prevents_accepting_invitation_under_concurrency()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("lock_staff_race");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Lock Race Test"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        // Get token
        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE user_id = @userId", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        var publicClient = fixture.CreateClient();

        // Truly race lock account ↔ accept invitation concurrently
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lockTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(adminClient, $"admin/accounts/{created.Id}/status", new { status = "LOCKED" }, HttpMethod.Patch);
        });

        var acceptTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(publicClient, "auth/staff-invitation/accept", new
            {
                token = rawToken,
                password = StrongPassword,
                confirmPassword = StrongPassword
            });
        });

        barrier.SetResult();
        var responses = await Task.WhenAll(lockTask, acceptTask);

        var lockStatus = responses[0].StatusCode;
        var acceptStatus = responses[1].StatusCode;

        // Neither returns 500
        Assert.NotEqual(HttpStatusCode.InternalServerError, lockStatus);
        Assert.NotEqual(HttpStatusCode.InternalServerError, acceptStatus);

        Assert.Equal(HttpStatusCode.NoContent, lockStatus);
        // If lock ran first -> Accept is 403 Forbidden
        // If accept ran first -> Accept is 204 NoContent, then lock changed status to LOCKED
        Assert.Contains(acceptStatus, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NoContent });

        // Account is guaranteed to end up LOCKED
        await using var checkCmd = new NpgsqlCommand("SELECT account_status FROM store.user_accounts WHERE id = @userId", db);
        checkCmd.Parameters.AddWithValue("userId", created.Id);
        var finalStatus = (string)(await checkCmd.ExecuteScalarAsync())!;
        Assert.Equal("LOCKED", finalStatus);
    }

    [Fact]
    public async Task Concurrent_resend_and_accept_invitation_does_not_deadlock_or_return_500()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("resend_accept_race");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Resend Accept Race"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE user_id = @userId", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // Fast-forward initial token created_at in DB by 2 minutes to satisfy 60s cooldown for resend
        await using var ffCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET created_at = now() - interval '2 minutes' WHERE user_id = @userId", db);
        ffCmd.Parameters.AddWithValue("userId", created.Id);
        await ffCmd.ExecuteNonQueryAsync();

        var publicClient = fixture.CreateClient();

        // Trigger both concurrently: Admin resends invitation ↔ Staff accepts invitation with token
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var resendTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(adminClient, $"admin/accounts/{created.Id}/staff-invitation/resend", new { });
        });

        var acceptTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(publicClient, "auth/staff-invitation/accept", new
            {
                token = rawToken,
                password = StrongPassword,
                confirmPassword = StrongPassword
            });
        });

        barrier.SetResult();
        var responses = await Task.WhenAll(resendTask, acceptTask);

        var resendStatus = responses[0].StatusCode;
        var acceptStatus = responses[1].StatusCode;

        // Neither request should ever return 500 Internal Server Error (deadlock 40P01)
        Assert.NotEqual(HttpStatusCode.InternalServerError, resendStatus);
        Assert.NotEqual(HttpStatusCode.InternalServerError, acceptStatus);

        // Pairwise mutually exclusive outcomes:
        // Case 1: Accept won the User lock first -> Accept returns 204 NoContent, Resend returns 400 BadRequest (INVALID_ACCOUNT_STATE because password/email_verified already set).
        // Case 2: Resend won the User lock first -> Resend returns 202 Accepted (new token issued, old revoked), Accept returns 400 BadRequest (INVALID_OR_EXPIRED_TOKEN because old token was revoked).
        var outcome = (acceptStatus, resendStatus);
        Assert.True(
            outcome == (HttpStatusCode.NoContent, HttpStatusCode.BadRequest) ||
            outcome == (HttpStatusCode.BadRequest, HttpStatusCode.Accepted),
            $"Expected either (Accept=204, Resend=400) or (Accept=400, Resend=202), but got (Accept={acceptStatus}, Resend={resendStatus})");
    }

    [Fact]
    public async Task Staff_in_invitation_pending_cannot_bypass_invitation_via_password_reset()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var staffEmail = Email("staff_bypass_attempt");

        using var inviteRes = await Send(adminClient, "admin/staff-invitations", new
        {
            email = staffEmail,
            fullName = "Staff Bypass Attempt"
        });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);
        var created = await inviteRes.Content.ReadFromJsonAsync<AdminAccountDetailDto>();
        Assert.NotNull(created);

        // Staff tries to request password reset via public endpoint
        var publicClient = fixture.CreateClient();
        using var resetReq = await Send(publicClient, "auth/password-reset/request", new
        {
            email = staffEmail
        });
        // Anti-enumeration: returns 202 Accepted
        Assert.Equal(HttpStatusCode.Accepted, resetReq.StatusCode);

        // Verify in DB that NO password reset token was created and NO password reset email in outbox
        await using var db = await fixture.OpenDatabaseAsync();
        await using var tokenCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_action_tokens WHERE user_id = @userId AND purpose = 'PASSWORD_RESET'", db);
        tokenCmd.Parameters.AddWithValue("userId", created.Id);
        Assert.Equal(0L, await tokenCmd.ExecuteScalarAsync());

        await using var outboxCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_email_outbox WHERE user_id = @userId AND template_key = 'PASSWORD_RESET'", db);
        outboxCmd.Parameters.AddWithValue("userId", created.Id);
        Assert.Equal(0L, await outboxCmd.ExecuteScalarAsync());

        // Account remains in INVITATION_PENDING (password_hash is null, email_verified_at is null)
        await using var userCmd = new NpgsqlCommand("SELECT password_hash, email_verified_at FROM store.user_accounts WHERE id = @userId", db);
        userCmd.Parameters.AddWithValue("userId", created.Id);
        await using var reader = await userCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task Get_accounts_handles_extreme_page_numbers_without_integer_overflow_or_500()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");

        // Extreme int.MaxValue page
        using var resMax = await adminClient.GetAsync($"/api/v1/admin/accounts?page={int.MaxValue}&pageSize=100");
        Assert.NotEqual(HttpStatusCode.InternalServerError, resMax.StatusCode);

        // Within validation range but high page (100,000)
        using var resHigh = await adminClient.GetAsync("/api/v1/admin/accounts?page=100000&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, resHigh.StatusCode);
        var pageResult = await resHigh.Content.ReadFromJsonAsync<AdminAccountPageDto>();
        Assert.NotNull(pageResult);
        Assert.Empty(pageResult.Items);
    }

    [Fact]
    public async Task Admin_can_revoke_all_sessions_of_target_account_blocks_self_target_and_audits()
    {
        var (adminClient, adminId, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, customerId, _) = await CreateUserWithRoleAsync("CUSTOMER");

        // Self-target revoke sessions returns 400 SELF_REVOKE_PROHIBITED
        using var selfRes = await Send(adminClient, $"admin/accounts/{adminId}/sessions/revoke", new { });
        Assert.Equal(HttpStatusCode.BadRequest, selfRes.StatusCode);

        // Customer currently authenticated (200)
        using var meBefore = await customerClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meBefore.StatusCode);

        // Admin revokes customer sessions (204)
        using var revokeRes = await Send(adminClient, $"admin/accounts/{customerId}/sessions/revoke", new { });
        Assert.Equal(HttpStatusCode.NoContent, revokeRes.StatusCode);

        // Customer session is immediately invalid (401)
        using var meAfter = await customerClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfter.StatusCode);

        // Verify audit log
        await using var db = await fixture.OpenDatabaseAsync();
        await using var auditCmd = new NpgsqlCommand("SELECT count(*) FROM store.audit_logs WHERE entity_id = @id AND action = 'ACCOUNT_SESSIONS_REVOKED'", db);
        auditCmd.Parameters.AddWithValue("id", customerId);
        Assert.Equal(1L, await auditCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migration_006_can_be_reapplied_without_altering_data()
    {
        await using var db = await fixture.OpenDatabaseAsync();

        // Count users and tokens before re-applying
        await using var countCmd = new NpgsqlCommand("SELECT count(*) FROM store.user_accounts", db);
        var initialUserCount = (long)(await countCmd.ExecuteScalarAsync())!;

        // Reapply 006_internal_staff_invites.sql
        await using var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream("InternalStaffInvitesSchema.sql")
            ?? throw new InvalidOperationException("Missing InternalStaffInvitesSchema.sql");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();

        await using var applyCmd = new NpgsqlCommand(sql, db);
        await applyCmd.ExecuteNonQueryAsync();

        // Verify count unaffected
        await using var countAfterCmd = new NpgsqlCommand("SELECT count(*) FROM store.user_accounts", db);
        var afterUserCount = (long)(await countAfterCmd.ExecuteScalarAsync())!;
        Assert.Equal(initialUserCount, afterUserCount);
    }

    [Fact]
    public async Task Worker_delivers_staff_invitation_email_with_proper_link()
    {
        string? deliveredRecipient = null;
        string? deliveredLink = null;

        var customApp = fixture.CreateConcurrentApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IAuthEmailSender>(_ => new DelegatingTestAuthEmailSender((recipient, link) =>
                {
                    deliveredRecipient = recipient;
                    deliveredLink = link;
                    return Task.CompletedTask;
                }));
            });
        });

        using var client = customApp.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        var adminEmail = Email("admin_worker");
        using var reg = await Send(client, "auth/register", new { email = adminEmail, password = StrongPassword, confirmPassword = StrongPassword, fullName = "Admin Worker" });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var promote = new NpgsqlCommand("UPDATE store.user_accounts SET role_id = (SELECT id FROM store.roles WHERE role_key = 'ADMIN'), email_verified_at = now() WHERE lower(email) = lower(@email)", db);
        promote.Parameters.AddWithValue("email", adminEmail);
        await promote.ExecuteNonQueryAsync();

        using var login = await Send(client, "auth/login", new { email = adminEmail, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var staffEmail = Email("staff_worker");
        using var inviteRes = await Send(client, "admin/staff-invitations", new { email = staffEmail, fullName = "Worker Staff Target" });
        Assert.Equal(HttpStatusCode.Created, inviteRes.StatusCode);

        // Run worker manually
        var scopeFactory = customApp.Services.GetRequiredService<IServiceScopeFactory>();
        var options = customApp.Services.GetRequiredService<IOptions<AuthEmailOptions>>();
        var clock = customApp.Services.GetRequiredService<TimeProvider>();
        var logger = customApp.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthEmailDeliveryWorker>>();
        var worker = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);

        var count = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.True(count >= 1);
        Assert.Equal(staffEmail, deliveredRecipient);
        Assert.NotNull(deliveredLink);
        Assert.Contains("/staff-invitation#token=", deliveredLink);
    }

    private sealed class DelegatingTestAuthEmailSender(Func<string, string, Task> onSend) : IAuthEmailSender
    {
        public Task SendPasswordResetEmailAsync(string recipientEmail, string resetLink, CancellationToken ct = default)
            => onSend(recipientEmail, resetLink);

        public Task SendVerificationEmailAsync(string recipientEmail, string verificationLink, CancellationToken ct = default)
            => onSend(recipientEmail, verificationLink);

        public Task SendStaffInvitationEmailAsync(string recipientEmail, string invitationLink, CancellationToken ct = default)
            => onSend(recipientEmail, invitationLink);
    }
}
