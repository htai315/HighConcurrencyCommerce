using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class AuthRecoveryTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string InitialPassword = "OldSecurePassword123!@#";
    private const string NewPassword = "NewSecurePassword456!@#";

    private static string Email() => $"recovery_{Guid.NewGuid():N}@example.invalid";

    private async Task ClearOutboxAsync()
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM store.auth_email_outbox", db);
        await cmd.ExecuteNonQueryAsync();
    }

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

    private static async Task RegisterUnverified(HttpClient client, string email)
    {
        using var response = await Send(client, "auth/register", new
        {
            email,
            password = InitialPassword,
            confirmPassword = InitialPassword,
            fullName = "Recovery User"
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Registration_creates_unverified_account_and_outbox_entry_with_protected_token()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        await using var db = await fixture.OpenDatabaseAsync();
        // 1. Verify user_accounts has email_verified_at IS NULL
        await using var userCmd = new NpgsqlCommand("SELECT id, email_verified_at FROM store.user_accounts WHERE lower(email)=lower(@email)", db);
        userCmd.Parameters.AddWithValue("email", email);
        await using var userReader = await userCmd.ExecuteReaderAsync();
        Assert.True(await userReader.ReadAsync());
        var userId = userReader.GetGuid(0);
        Assert.True(await userReader.IsDBNullAsync(1));
        await userReader.CloseAsync();

        // 2. Verify auth_action_tokens has one VERIFY_EMAIL token
        await using var tokenCmd = new NpgsqlCommand("SELECT token_hash, purpose, consumed_at, revoked_at FROM store.auth_action_tokens WHERE user_id=@userId", db);
        tokenCmd.Parameters.AddWithValue("userId", userId);
        await using var tokenReader = await tokenCmd.ExecuteReaderAsync();
        Assert.True(await tokenReader.ReadAsync());
        var tokenHash = tokenReader.GetString(0);
        var purpose = tokenReader.GetString(1);
        Assert.Equal(64, tokenHash.Length);
        Assert.Equal("VERIFY_EMAIL", purpose);
        Assert.True(await tokenReader.IsDBNullAsync(2));
        Assert.True(await tokenReader.IsDBNullAsync(3));
        await tokenReader.CloseAsync();

        // 3. Verify auth_email_outbox has encrypted protected_payload
        await using var outboxCmd = new NpgsqlCommand("SELECT template_key, recipient_email, protected_payload, sent_at FROM store.auth_email_outbox WHERE user_id=@userId", db);
        outboxCmd.Parameters.AddWithValue("userId", userId);
        await using var outboxReader = await outboxCmd.ExecuteReaderAsync();
        Assert.True(await outboxReader.ReadAsync());
        Assert.Equal("VERIFY_EMAIL", outboxReader.GetString(0));
        Assert.Equal(email, outboxReader.GetString(1));
        var protectedPayload = outboxReader.GetString(2);
        Assert.NotEmpty(protectedPayload);
        // The protected payload should never be plaintext token or token hash
        Assert.NotEqual(tokenHash, protectedPayload);
        await outboxReader.CloseAsync();
    }

    [Fact]
    public async Task Login_without_verification_returns_403_EMAIL_NOT_VERIFIED_only_after_password_matches()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        // Wrong password returns 401 INVALID_CREDENTIALS (no leak that user exists/is unverified)
        using var wrongLogin = await Send(client, "auth/login", new { email, password = "WrongPassword123!@" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongLogin.StatusCode);
        var wrongErr = await wrongLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_CREDENTIALS", wrongErr.GetProperty("code").GetString());

        // Correct password returns 403 EMAIL_NOT_VERIFIED
        using var unverifiedLogin = await Send(client, "auth/login", new { email, password = InitialPassword });
        Assert.Equal(HttpStatusCode.Forbidden, unverifiedLogin.StatusCode);
        var unverifiedErr = await unverifiedLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EMAIL_NOT_VERIFIED", unverifiedErr.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Email_verification_confirm_sets_verified_at_and_replay_is_idempotent()
    {
        await using var app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);

        // Retrieve outbox payload
        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='VERIFY_EMAIL'", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // Confirm email with token
        using var confirmRes = await Send(client, "auth/email-verification/confirm", new { token = rawToken });
        Assert.Equal(HttpStatusCode.NoContent, confirmRes.StatusCode);

        // Verify user account in db now has email_verified_at set
        await using var verifyCmd = new NpgsqlCommand("SELECT email_verified_at FROM store.user_accounts WHERE lower(email)=lower(@email)", db);
        verifyCmd.Parameters.AddWithValue("email", email);
        var verifiedAt = await verifyCmd.ExecuteScalarAsync();
        Assert.NotNull(verifiedAt);
        Assert.NotEqual(DBNull.Value, verifiedAt);

        // Replay confirmation: should be idempotent and return 204 NoContent
        using var replayRes = await Send(client, "auth/email-verification/confirm", new { token = rawToken });
        Assert.Equal(HttpStatusCode.NoContent, replayRes.StatusCode);

        // Now login succeeds
        using var loginRes = await Send(client, "auth/login", new { email, password = InitialPassword });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
    }

    [Fact]
    public async Task Password_reset_request_and_confirm_flow_revokes_sessions_and_prevents_replay()
    {
        await using var app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);
        await fixture.VerifyUserAsync(email);

        // Login to get an active session
        using var initialLogin = await Send(client, "auth/login", new { email, password = InitialPassword });
        Assert.Equal(HttpStatusCode.OK, initialLogin.StatusCode);
        var meRes = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);

        // 1. Request password reset
        using var requestRes = await Send(client, "auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, requestRes.StatusCode);
        var neutralBody = await requestRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.", neutralBody.GetProperty("message").GetString());

        // Extract token
        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='PASSWORD_RESET' ORDER BY created_at DESC LIMIT 1", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // 2. Reject reset with same password
        using var samePassRes = await Send(client, "auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = InitialPassword,
            confirmPassword = InitialPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, samePassRes.StatusCode);
        var sameErr = await samePassRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PASSWORD_RESET_SAME_AS_CURRENT", sameErr.GetProperty("code").GetString());

        // 3. Confirm reset with new password
        using var confirmPassRes = await Send(client, "auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = NewPassword,
            confirmPassword = NewPassword
        });
        Assert.Equal(HttpStatusCode.NoContent, confirmPassRes.StatusCode);

        // 4. Old session must be revoked
        var meAfterRevoke = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterRevoke.StatusCode);

        // 5. Old password fails
        using var oldLogin = await Send(client, "auth/login", new { email, password = InitialPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        // 6. New password succeeds
        using var newLogin = await Send(client, "auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

        // 7. Token replay fails with INVALID_OR_EXPIRED_TOKEN
        using var replayReset = await Send(client, "auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = "YetAnotherPassword789!@",
            confirmPassword = "YetAnotherPassword789!@"
        });
        Assert.Equal(HttpStatusCode.BadRequest, replayReset.StatusCode);
        var replayErr = await replayReset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_OR_EXPIRED_TOKEN", replayErr.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Anti_enumeration_password_reset_returns_202_for_non_existent_and_locked_accounts()
    {
        using var client = fixture.CreateClient();

        // 1. Non-existent email returns neutral 202
        using var nonExistent = await Send(client, "auth/password-reset/request", new { email = "doesnotexist@example.invalid" });
        Assert.Equal(HttpStatusCode.Accepted, nonExistent.StatusCode);
        var body1 = await nonExistent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.", body1.GetProperty("message").GetString());

        // 2. Locked user returns neutral 202 without queuing outbox
        var lockedEmail = Email();
        await RegisterUnverified(client, lockedEmail);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var lockCmd = new NpgsqlCommand("UPDATE store.user_accounts SET account_status='LOCKED' WHERE lower(email)=lower(@email)", db);
        lockCmd.Parameters.AddWithValue("email", lockedEmail);
        await lockCmd.ExecuteNonQueryAsync();

        using var lockedRes = await Send(client, "auth/password-reset/request", new { email = lockedEmail });
        Assert.Equal(HttpStatusCode.Accepted, lockedRes.StatusCode);
        var body2 = await lockedRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.", body2.GetProperty("message").GetString());

        // Verify no password reset outbox was queued for locked user
        await using var checkOutbox = new NpgsqlCommand("SELECT count(*) FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='PASSWORD_RESET'", db);
        checkOutbox.Parameters.AddWithValue("email", lockedEmail);
        Assert.Equal(0L, await checkOutbox.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Resend_verification_enforces_60s_cooldown_per_account()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var countCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_action_tokens WHERE lower(email_snapshot)=lower(@email) AND purpose='VERIFY_EMAIL'", db);
        countCmd.Parameters.AddWithValue("email", email);
        Assert.Equal(1L, await countCmd.ExecuteScalarAsync());

        // Immediate resend request within 60s
        using var resendRes = await Send(client, "auth/email-verification/resend", new { email });
        Assert.Equal(HttpStatusCode.Accepted, resendRes.StatusCode);

        // Token count should still be 1 (suppressed due to cooldown)
        Assert.Equal(1L, await countCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concurrent_password_reset_confirm_only_one_succeeds_no_500()
    {
        await using var app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);
        await fixture.VerifyUserAsync(email);

        using var req = await Send(client, "auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, req.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='PASSWORD_RESET' ORDER BY created_at DESC LIMIT 1", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        using var client1 = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var client2 = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var task1 = Send(client1, "auth/password-reset/confirm", new { token = rawToken, newPassword = NewPassword, confirmPassword = NewPassword });
        var task2 = Send(client2, "auth/password-reset/confirm", new { token = rawToken, newPassword = NewPassword, confirmPassword = NewPassword });

        var responses = await Task.WhenAll(task1, task2);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        // Exactly one should succeed (204) and the other should fail (400)
        Assert.Contains(HttpStatusCode.NoContent, statuses);
        Assert.Contains(HttpStatusCode.BadRequest, statuses);
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statuses);
    }

    [Fact]
    public async Task Missing_CSRF_token_returns_400_CSRF_INVALID_on_recovery_endpoints()
    {
        using var client = fixture.CreateClient();
        using var res = await Send(client, "auth/password-reset/request", new { email = "test@example.invalid" }, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CSRF_INVALID", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuthEmailDeliveryWorker_processes_outbox_batch_and_discards_obsolete_token()
    {
        await ClearOutboxAsync();
        await using var app = fixture.CreateConcurrentApplication();
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        var options = app.Services.GetRequiredService<IOptions<AuthEmailOptions>>();
        var clock = app.Services.GetRequiredService<TimeProvider>();
        var logger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthEmailDeliveryWorker>>();

        var worker = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);

        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);

        await using var db = await fixture.OpenDatabaseAsync();
        // Consume the token directly to simulate an obsolete job before worker runs
        await using var consumeCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET consumed_at=now() WHERE lower(email_snapshot)=lower(@email)", db);
        consumeCmd.Parameters.AddWithValue("email", email);
        await consumeCmd.ExecuteNonQueryAsync();

        // Worker processes batch
        var processed = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.True(processed >= 1);

        // Check that obsolete outbox item was marked discarded_at
        await using var checkCmd = new NpgsqlCommand("SELECT discarded_at, sent_at FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email)", db);
        checkCmd.Parameters.AddWithValue("email", email);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.IsDBNullAsync(0)); // discarded_at is NOT null
        Assert.True(await reader.IsDBNullAsync(1));  // sent_at is null
    }

    [Fact]
    public async Task Running_migration_multiple_times_does_not_verify_unverified_accounts()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        await using var db = await fixture.OpenDatabaseAsync();
        // 1. Check email_verified_at is null
        await using (var checkCmd = new NpgsqlCommand("SELECT email_verified_at FROM store.user_accounts WHERE lower(email)=lower(@email)", db))
        {
            checkCmd.Parameters.AddWithValue("email", email);
            var verifiedAt = await checkCmd.ExecuteScalarAsync();
            Assert.True(verifiedAt == null || verifiedAt == DBNull.Value);
        }

        // 2. Re-run migration 005 from embedded resource (same as --migrate-auth)
        await using (var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream("AuthEmailRecoverySchema.sql")
            ?? throw new InvalidOperationException("Missing AuthEmailRecoverySchema.sql"))
        {
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            await using var migrateCmd = new NpgsqlCommand(sql, db);
            await migrateCmd.ExecuteNonQueryAsync();
        }

        // 3. email_verified_at must STILL be null
        await using (var checkAgainCmd = new NpgsqlCommand("SELECT email_verified_at FROM store.user_accounts WHERE lower(email)=lower(@email)", db))
        {
            checkAgainCmd.Parameters.AddWithValue("email", email);
            var verifiedAt = await checkAgainCmd.ExecuteScalarAsync();
            Assert.True(verifiedAt == null || verifiedAt == DBNull.Value);
        }

        // 4. Login attempt must still fail with 403 EMAIL_NOT_VERIFIED
        using var loginRes = await Send(client, "auth/login", new { email, password = InitialPassword });
        Assert.Equal(HttpStatusCode.Forbidden, loginRes.StatusCode);
    }

    [Fact]
    public async Task AuthEmailDeliveryWorker_does_not_hold_db_transaction_during_smtp_and_delivers_successfully()
    {
        await ClearOutboxAsync();
        var lockProbeSucceeded = false;

        var customApp = fixture.CreateConcurrentApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuthEmailSender));
                if (descriptor != null) services.Remove(descriptor);

                var hostedWorker = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(AuthEmailDeliveryWorker));
                if (hostedWorker != null) services.Remove(hostedWorker);

                services.AddScoped<IAuthEmailSender>(_ => new DelegatingAuthEmailSender(async (email, _) =>
                {
                    // While this simulated SMTP call is in flight, prove no DB transaction/row lock is held!
                    await using var probeDb = await fixture.OpenDatabaseAsync();
                    await using var probeTx = await probeDb.BeginTransactionAsync();
                    // Attempt to lock all outbox rows with NOWAIT. If a transaction or lock was held, this would throw 55P03!
                    await using var probeCmd = new NpgsqlCommand("SELECT * FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) FOR UPDATE NOWAIT", probeDb, probeTx);
                    probeCmd.Parameters.AddWithValue("email", email);
                    await using var reader = await probeCmd.ExecuteReaderAsync();
                    lockProbeSucceeded = await reader.ReadAsync();
                    await reader.CloseAsync();
                    await probeTx.CommitAsync();
                }));
            });
        });

        using var client = customApp.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);

        var scopeFactory = customApp.Services.GetRequiredService<IServiceScopeFactory>();
        var options = customApp.Services.GetRequiredService<IOptions<AuthEmailOptions>>();
        var clock = customApp.Services.GetRequiredService<TimeProvider>();
        var logger = customApp.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthEmailDeliveryWorker>>();

        var worker = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);
        var processed = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.True(processed >= 1);

        // Verify probe succeeded without blocking or throwing 55P03 lock_not_available
        Assert.True(lockProbeSucceeded);

        // Verify outbox record now marked sent_at
        await using var db = await fixture.OpenDatabaseAsync();
        await using var verifyCmd = new NpgsqlCommand("SELECT sent_at, locked_at, locked_by FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email)", db);
        verifyCmd.Parameters.AddWithValue("email", email);
        await using var verifyReader = await verifyCmd.ExecuteReaderAsync();
        Assert.True(await verifyReader.ReadAsync());
        Assert.False(await verifyReader.IsDBNullAsync(0)); // sent_at is NOT null
        Assert.True(await verifyReader.IsDBNullAsync(1));  // locked_at is null
        Assert.True(await verifyReader.IsDBNullAsync(2));  // locked_by is null
    }

    [Fact]
    public async Task AuthEmailDeliveryWorker_recovers_expired_leases_and_handles_long_error_codes()
    {
        // 1. SanitizeErrorCode unit assertions - structured codes only, no provider message leak
        var longException = new InvalidOperationException(new string('E', 500));
        var sanitizedCode = AuthEmailDeliveryWorker.SanitizeErrorCode(longException);
        Assert.True(sanitizedCode.Length <= 64);
        Assert.Equal("InvalidOperationException", sanitizedCode);

        var longSmtp = new System.Net.Mail.SmtpException(System.Net.Mail.SmtpStatusCode.MailboxUnavailable, new string('S', 300));
        var sanitizedSmtpCode = AuthEmailDeliveryWorker.SanitizeErrorCode(longSmtp);
        Assert.True(sanitizedSmtpCode.Length <= 64);
        Assert.Equal("SMTP_550_MailboxUnavailable", sanitizedSmtpCode);

        await ClearOutboxAsync();

        // 2. Custom app with failing email sender that throws > 64 chars message
        var customApp = fixture.CreateConcurrentApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuthEmailSender));
                if (descriptor != null) services.Remove(descriptor);

                var hostedWorker = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(AuthEmailDeliveryWorker));
                if (hostedWorker != null) services.Remove(hostedWorker);

                services.AddScoped<IAuthEmailSender>(_ => new DelegatingAuthEmailSender((_, _) =>
                    throw new System.Net.Mail.SmtpException(System.Net.Mail.SmtpStatusCode.GeneralFailure, new string('X', 200))));
            });
        });

        using var client = customApp.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);

        // 3. Simulate a crashed worker lease on this outbox row (locked 10 minutes ago)
        await using var db = await fixture.OpenDatabaseAsync();
        await using var crashCmd = new NpgsqlCommand(
            "UPDATE store.auth_email_outbox SET locked_at = now() - interval '10 minutes', locked_by = 'crashed_worker_dead' WHERE lower(recipient_email)=lower(@email)", db);
        crashCmd.Parameters.AddWithValue("email", email);
        await crashCmd.ExecuteNonQueryAsync();

        // 4. New worker processes batch
        var scopeFactory = customApp.Services.GetRequiredService<IServiceScopeFactory>();
        var options = customApp.Services.GetRequiredService<IOptions<AuthEmailOptions>>();
        var clock = customApp.Services.GetRequiredService<TimeProvider>();
        var logger = customApp.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthEmailDeliveryWorker>>();

        var worker = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);
        var processed = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.True(processed >= 1);

        // 5. Verify the item was recovered, attempt_count incremented to 1, last_error_code <= 64 chars, lease cleared
        await using var verifyDb = await fixture.OpenDatabaseAsync();
        await using var verifyCmd = new NpgsqlCommand(
            "SELECT attempt_count, last_error_code, locked_at, locked_by FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email)", verifyDb);
        verifyCmd.Parameters.AddWithValue("email", email);
        await using var reader = await verifyCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        var storedError = reader.GetString(1);
        Assert.True(storedError.Length <= 64);
        Assert.True(await reader.IsDBNullAsync(2)); // locked_at cleared
        Assert.True(await reader.IsDBNullAsync(3)); // locked_by cleared
    }

    [Fact]
    public async Task AuthEmailDeliveryWorker_concurrent_workers_claim_disjoint_batches_without_double_sending()
    {
        await ClearOutboxAsync();
        var sentEmails = new System.Collections.Concurrent.ConcurrentBag<string>();
        var customApp = fixture.CreateConcurrentApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuthEmailSender));
                if (descriptor != null) services.Remove(descriptor);

                var hostedWorker = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(AuthEmailDeliveryWorker));
                if (hostedWorker != null) services.Remove(hostedWorker);

                services.AddScoped<IAuthEmailSender>(_ => new DelegatingAuthEmailSender((email, _) =>
                {
                    sentEmails.Add(email);
                    return Task.CompletedTask;
                }));
            });
        });

        using var client = customApp.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var emails = Enumerable.Range(0, 6).Select(_ => Email()).ToList();
        foreach (var em in emails)
        {
            await RegisterUnverified(client, em);
        }

        var scopeFactory = customApp.Services.GetRequiredService<IServiceScopeFactory>();
        var options = customApp.Services.GetRequiredService<IOptions<AuthEmailOptions>>();
        var clock = customApp.Services.GetRequiredService<TimeProvider>();
        var logger = customApp.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthEmailDeliveryWorker>>();

        var worker1 = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);
        var worker2 = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);

        // Run both workers concurrently
        var results = await Task.WhenAll(
            worker1.ProcessBatchAsync(CancellationToken.None),
            worker2.ProcessBatchAsync(CancellationToken.None)
        );

        var sentForTest = sentEmails.Where(e => emails.Contains(e)).ToList();
        Assert.Equal(6, sentForTest.Count);
        Assert.Equal(6, sentForTest.Distinct().Count());
    }

    [Fact]
    public async Task Token_request_rejects_tokens_exceeding_128_characters()
    {
        using var client = fixture.CreateClient();
        var longToken = new string('a', 129);

        // 1. Password reset confirm with token > 128
        using var resetRes = await Send(client, "auth/password-reset/confirm", new
        {
            token = longToken,
            newPassword = NewPassword,
            confirmPassword = NewPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, resetRes.StatusCode);

        // 2. Email verification confirm with token > 128
        using var verifyRes = await Send(client, "auth/email-verification/confirm", new
        {
            token = longToken
        });
        Assert.Equal(HttpStatusCode.BadRequest, verifyRes.StatusCode);
    }

    [Fact]
    public async Task AuthEmailDeliveryWorker_persists_outcomes_per_item_preventing_lease_exhaustion_on_slow_smtp()
    {
        await ClearOutboxAsync();
        var email2Started = new TaskCompletionSource<bool>();
        var email2CanProceed = new TaskCompletionSource<bool>();
        string? firstSentEmail = null;

        var customApp = fixture.CreateConcurrentApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuthEmailSender));
                if (descriptor != null) services.Remove(descriptor);

                var hostedWorker = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(AuthEmailDeliveryWorker));
                if (hostedWorker != null) services.Remove(hostedWorker);

                var sendCount = 0;
                services.AddScoped<IAuthEmailSender>(_ => new DelegatingAuthEmailSender(async (email, _) =>
                {
                    var count = Interlocked.Increment(ref sendCount);
                    if (count == 1)
                    {
                        firstSentEmail = email;
                    }
                    else if (count == 2)
                    {
                        email2Started.TrySetResult(true);
                        await email2CanProceed.Task;
                    }
                }));
            });
        });

        using var client = customApp.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email1 = Email();
        var email2 = Email();
        await RegisterUnverified(client, email1);
        await RegisterUnverified(client, email2);

        var scopeFactory = customApp.Services.GetRequiredService<IServiceScopeFactory>();
        var options = customApp.Services.GetRequiredService<IOptions<AuthEmailOptions>>();
        var clock = customApp.Services.GetRequiredService<TimeProvider>();
        var logger = customApp.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthEmailDeliveryWorker>>();

        var worker = new AuthEmailDeliveryWorker(scopeFactory, options, clock, logger);
        var workerTask = Task.Run(async () => await worker.ProcessBatchAsync(CancellationToken.None));

        // Wait until email2 has started sending (meaning email1 has completely finished sending & persisting)
        await email2Started.Task;

        // While the second email is still paused in flight, the first email must ALREADY be committed to DB with sent_at != null and lease cleared!
        Assert.NotNull(firstSentEmail);
        await using (var db = await fixture.OpenDatabaseAsync())
        {
            await using var checkCmd = new NpgsqlCommand("SELECT sent_at, locked_at, locked_by FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email)", db);
            checkCmd.Parameters.AddWithValue("email", firstSentEmail);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(await reader.IsDBNullAsync(0)); // sent_at is already set!
            Assert.True(await reader.IsDBNullAsync(1));  // locked_at is already cleared!
            Assert.True(await reader.IsDBNullAsync(2));  // locked_by is already cleared!
        }

        // Allow email2 to complete
        email2CanProceed.TrySetResult(true);
        var totalProcessed = await workerTask;
        Assert.True(totalProcessed >= 2);
    }

    [Fact]
    public async Task Concurrent_request_and_reset_password_does_not_deadlock_or_return_500()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        // Verify email first so account has active password
        await using var db = await fixture.OpenDatabaseAsync();
        await using var verifyCmd = new NpgsqlCommand("UPDATE store.user_accounts SET email_verified_at = now() WHERE lower(email)=lower(@email)", db);
        verifyCmd.Parameters.AddWithValue("email", email);
        await verifyCmd.ExecuteNonQueryAsync();

        // Request initial password reset to obtain token
        using var initReq = await Send(client, "auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, initReq.StatusCode);

        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='PASSWORD_RESET'", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        var client1 = fixture.CreateClient();
        var client2 = fixture.CreateClient();

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var requestTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(client1, "auth/password-reset/request", new { email });
        });

        var resetTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(client2, "auth/password-reset/confirm", new
            {
                token = rawToken,
                newPassword = NewPassword,
                confirmPassword = NewPassword
            });
        });

        barrier.SetResult();
        var responses = await Task.WhenAll(requestTask, resetTask);

        var requestStatus = responses[0].StatusCode;
        var resetStatus = responses[1].StatusCode;

        // Neither request returns 500 (deadlock 40P01 eliminated)
        Assert.NotEqual(HttpStatusCode.InternalServerError, requestStatus);
        Assert.NotEqual(HttpStatusCode.InternalServerError, resetStatus);

        Assert.Equal(HttpStatusCode.Accepted, requestStatus);
        Assert.Contains(resetStatus, new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task Concurrent_resend_and_confirm_email_does_not_deadlock_or_return_500()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='VERIFY_EMAIL'", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = fixture.CreateConcurrentApplication().Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // Fast-forward initial token created_at in DB by 2 minutes to satisfy 60s cooldown for resend
        await using var ffCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET created_at = now() - interval '2 minutes' WHERE email_snapshot = @email", db);
        ffCmd.Parameters.AddWithValue("email", email);
        await ffCmd.ExecuteNonQueryAsync();

        var client1 = fixture.CreateClient();
        var client2 = fixture.CreateClient();

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var resendTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(client1, "auth/email-verification/resend", new { email });
        });

        var confirmTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await Send(client2, "auth/email-verification/confirm", new { token = rawToken });
        });

        barrier.SetResult();
        var responses = await Task.WhenAll(resendTask, confirmTask);

        var resendStatus = responses[0].StatusCode;
        var confirmStatus = responses[1].StatusCode;

        // Neither returns 500
        Assert.NotEqual(HttpStatusCode.InternalServerError, resendStatus);
        Assert.NotEqual(HttpStatusCode.InternalServerError, confirmStatus);

        Assert.Equal(HttpStatusCode.Accepted, resendStatus);
        Assert.Contains(confirmStatus, new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task Customer_with_unverified_email_cannot_request_password_reset()
    {
        using var client = fixture.CreateClient();
        var email = Email();
        await RegisterUnverified(client, email);

        // Request password reset on unverified account
        using var res = await Send(client, "auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        // Anti-enumeration 202 returned, but NO reset token or outbox queued
        await using var db = await fixture.OpenDatabaseAsync();
        await using var tokenCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_action_tokens WHERE lower(email_snapshot) = lower(@email) AND purpose = 'PASSWORD_RESET'", db);
        tokenCmd.Parameters.AddWithValue("email", email);
        Assert.Equal(0L, await tokenCmd.ExecuteScalarAsync());

        await using var outboxCmd = new NpgsqlCommand("SELECT count(*) FROM store.auth_email_outbox WHERE lower(recipient_email) = lower(@email) AND template_key = 'PASSWORD_RESET'", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        Assert.Equal(0L, await outboxCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Google_only_user_can_request_password_reset_and_set_password_while_keeping_google_identity()
    {
        await using var app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        // 1. Create a Google-only account (PasswordHash == null, EmailVerifiedAt != null, ExternalIdentity exists)
        var email = Email();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = await fixture.OpenDatabaseAsync())
        {
            await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = 'CUSTOMER'", db);
            var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

            await using var insertUserCmd = new NpgsqlCommand(@"
                INSERT INTO store.user_accounts (id, email, full_name, role_id, account_status, password_hash, email_verified_at, created_at, updated_at)
                VALUES (@id, @email, 'Google Only User', @roleId, 'ACTIVE', NULL, @now, @now, @now)", db);
            insertUserCmd.Parameters.AddWithValue("id", userId);
            insertUserCmd.Parameters.AddWithValue("email", email);
            insertUserCmd.Parameters.AddWithValue("roleId", roleId);
            insertUserCmd.Parameters.AddWithValue("now", now);
            await insertUserCmd.ExecuteNonQueryAsync();

            await using var insertIdentCmd = new NpgsqlCommand(@"
                INSERT INTO store.external_identities (id, user_id, provider, subject, email, created_at)
                VALUES (@id, @userId, 'GOOGLE', @sub, @email, @now)", db);
            insertIdentCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            insertIdentCmd.Parameters.AddWithValue("userId", userId);
            insertIdentCmd.Parameters.AddWithValue("sub", $"google_sub_{Guid.NewGuid():N}");
            insertIdentCmd.Parameters.AddWithValue("email", email);
            insertIdentCmd.Parameters.AddWithValue("now", now);
            await insertIdentCmd.ExecuteNonQueryAsync();
        }

        // 2. Request password reset
        using var reqRes = await Send(client, "auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, reqRes.StatusCode);

        // 3. Extract generated reset token
        await using (var db = await fixture.OpenDatabaseAsync())
        {
            await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email) = lower(@email) AND template_key = 'PASSWORD_RESET' ORDER BY created_at DESC LIMIT 1", db);
            outboxCmd.Parameters.AddWithValue("email", email);
            var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

            var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
            var rawToken = protector.Unprotect(protectedPayload);

            // 4. Confirm password reset: set initial password
            using var confirmRes = await Send(client, "auth/password-reset/confirm", new
            {
                token = rawToken,
                newPassword = NewPassword,
                confirmPassword = NewPassword
            });
            Assert.Equal(HttpStatusCode.NoContent, confirmRes.StatusCode);

            // 5. Verify database: PasswordHash is set, Google external identity is preserved
            await using var verifyCmd = new NpgsqlCommand("SELECT password_hash, email_verified_at FROM store.user_accounts WHERE id = @userId", db);
            verifyCmd.Parameters.AddWithValue("userId", userId);
            await using var reader = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(await reader.IsDBNullAsync(0)); // password_hash is now populated
            Assert.False(await reader.IsDBNullAsync(1)); // email_verified_at preserved
            await reader.CloseAsync();

            await using var verifyGoogleCmd = new NpgsqlCommand("SELECT count(*) FROM store.external_identities WHERE user_id = @userId AND provider = 'GOOGLE'", db);
            verifyGoogleCmd.Parameters.AddWithValue("userId", userId);
            Assert.Equal(1L, await verifyGoogleCmd.ExecuteScalarAsync()); // Google identity intact
        }

        // 6. User can now login with password!
        using var loginRes = await Send(client, "auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
    }

    [Fact]
    public async Task Password_reset_rejects_token_when_email_snapshot_does_not_match_user_under_lock()
    {
        await using var app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var email = Email();
        await RegisterUnverified(client, email);
        await fixture.VerifyUserAsync(email);

        using var req = await Send(client, "auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, req.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var outboxCmd = new NpgsqlCommand("SELECT protected_payload FROM store.auth_email_outbox WHERE lower(recipient_email)=lower(@email) AND template_key='PASSWORD_RESET' ORDER BY created_at DESC LIMIT 1", db);
        outboxCmd.Parameters.AddWithValue("email", email);
        var protectedPayload = (string)(await outboxCmd.ExecuteScalarAsync())!;

        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HCC.AuthEmail.v1");
        var rawToken = protector.Unprotect(protectedPayload);

        // Tamper with email_snapshot in database to simulate snapshot mismatch
        await using var tamperCmd = new NpgsqlCommand("UPDATE store.auth_action_tokens SET email_snapshot = 'mismatch@example.invalid' WHERE lower(email_snapshot) = lower(@email)", db);
        tamperCmd.Parameters.AddWithValue("email", email);
        await tamperCmd.ExecuteNonQueryAsync();

        using var confirmRes = await Send(client, "auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = NewPassword,
            confirmPassword = NewPassword
        });

        Assert.Equal(HttpStatusCode.BadRequest, confirmRes.StatusCode);
        var body = await confirmRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_OR_EXPIRED_TOKEN", body.GetProperty("code").GetString());
    }

    private sealed class DelegatingAuthEmailSender(Func<string, string, Task> onSend) : IAuthEmailSender
    {
        public Task SendPasswordResetEmailAsync(string recipientEmail, string resetLink, CancellationToken ct = default)
            => onSend(recipientEmail, resetLink);

        public Task SendVerificationEmailAsync(string recipientEmail, string verificationLink, CancellationToken ct = default)
            => onSend(recipientEmail, verificationLink);

        public Task SendStaffInvitationEmailAsync(string recipientEmail, string invitationLink, CancellationToken ct = default)
            => onSend(recipientEmail, invitationLink);
    }
}

