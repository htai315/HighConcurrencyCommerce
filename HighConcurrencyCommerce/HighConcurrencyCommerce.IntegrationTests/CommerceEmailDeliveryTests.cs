using System.Collections.Concurrent;
using System.Net.Mail;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Wishlist;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CommerceEmailDeliveryTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task Delivery_holds_no_database_transaction_and_persists_success_immediately()
    {
        await ClearAsync();
        var user = await SeedUserAsync(verified: true);
        var outbox = await SeedOutboxAsync(user, "event-success");
        var lockProbe = false;
        CommerceAlertEmail? delivered = null;
        var sender = new DelegatingSender(async (email, ct) =>
        {
            delivered = email;
            await using var db = await fixture.OpenDatabaseAsync();
            await using var tx = await db.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT id FROM store.commerce_email_outbox WHERE id=@id FOR UPDATE NOWAIT", db, tx);
            command.Parameters.AddWithValue("id", outbox);
            lockProbe = await command.ExecuteScalarAsync(ct) is Guid;
            await tx.CommitAsync(ct);
        });

        Assert.Equal(1, await CreateWorker(sender).ProcessBatchAsync());
        Assert.True(lockProbe);
        Assert.NotNull(delivered);
        Assert.Equal("https://shop.example.test/products/11111111-1111-1111-1111-111111111111", delivered.ActionUrl);
        Assert.True(await ScalarAsync<bool>("""
            SELECT sent_at IS NOT NULL AND locked_at IS NULL AND locked_by IS NULL
            FROM store.commerce_email_outbox WHERE id=@id
            """, new NpgsqlParameter("id", outbox)));
    }

    [Fact]
    public async Task Concurrent_workers_claim_disjoint_batches_without_duplicate_delivery()
    {
        await ClearAsync();
        var user = await SeedUserAsync(verified: true);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await SeedOutboxAsync(user, $"concurrent-{i}"));
        var sent = new ConcurrentBag<string>();
        var sender = new DelegatingSender((email, _) =>
        {
            sent.Add(email.Subject);
            return Task.CompletedTask;
        });

        var counts = await Task.WhenAll(
            CreateWorker(sender).ProcessBatchAsync(),
            CreateWorker(sender).ProcessBatchAsync());
        Assert.Equal(6, counts.Sum());
        Assert.Equal(6, sent.Count);
        Assert.Equal(6, sent.Distinct().Count());
        Assert.Equal(6, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.commerce_email_outbox
            WHERE id = ANY(@ids) AND sent_at IS NOT NULL
            """, new NpgsqlParameter<Guid[]>("ids", ids.ToArray())));
    }

    [Fact]
    public async Task Delivery_persists_each_outcome_before_processing_the_next_slow_item()
    {
        await ClearAsync();
        var user = await SeedUserAsync(verified: true);
        var first = await SeedOutboxAsync(user, "per-item-first");
        var second = await SeedOutboxAsync(user, "per-item-second");
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new DelegatingSender(async (email, ct) =>
        {
            if (!email.Subject.Contains("per-item-second", StringComparison.Ordinal)) return;
            secondStarted.TrySetResult();
            await releaseSecond.Task.WaitAsync(ct);
        });

        var processing = CreateWorker(sender).ProcessBatchAsync();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await ScalarAsync<bool>("""
            SELECT sent_at IS NOT NULL AND locked_at IS NULL AND locked_by IS NULL
            FROM store.commerce_email_outbox WHERE id=@id
            """, new NpgsqlParameter("id", first)));
        Assert.True(await ScalarAsync<bool>("""
            SELECT sent_at IS NULL AND locked_at IS NOT NULL AND locked_by IS NOT NULL
            FROM store.commerce_email_outbox WHERE id=@id
            """, new NpgsqlParameter("id", second)));

        releaseSecond.TrySetResult();
        Assert.Equal(2, await processing);
    }

    [Fact]
    public async Task Expired_lease_and_smtp_failure_schedule_retry_with_sanitized_code()
    {
        await ClearAsync();
        var user = await SeedUserAsync(verified: true);
        var outbox = await SeedOutboxAsync(user, "smtp-failure");
        await ExecuteAsync("""
            UPDATE store.commerce_email_outbox
            SET locked_at=now()-interval '6 minutes', locked_by='crashed-worker'
            WHERE id=@id
            """, new NpgsqlParameter("id", outbox));
        var sender = new DelegatingSender((_, _) =>
            throw new SmtpException(System.Net.Mail.SmtpStatusCode.MailboxUnavailable, new string('x', 500)));

        Assert.Equal(1, await CreateWorker(sender).ProcessBatchAsync());
        Assert.True(await ScalarAsync<bool>("""
            SELECT attempt_count=1
               AND last_error_code='SMTP_550_MailboxUnavailable'
               AND length(last_error_code)<=64
               AND next_attempt_at>now()
               AND locked_at IS NULL AND locked_by IS NULL
               AND sent_at IS NULL AND discarded_at IS NULL
            FROM store.commerce_email_outbox WHERE id=@id
            """, new NpgsqlParameter("id", outbox)));
        Assert.Equal("SEND_TIMEOUT", CommerceEmailDeliveryWorker.SanitizeErrorCode(new OperationCanceledException()));
    }

    [Fact]
    public async Task Invalid_payload_and_ineligible_recipient_are_discarded_without_smtp()
    {
        await ClearAsync();
        var verified = await SeedUserAsync(verified: true);
        var unverified = await SeedUserAsync(verified: false);
        var invalid = await SeedOutboxAsync(verified, "invalid-payload", payload: "{\"actionPath\":\"https://evil.example\"}");
        var ineligible = await SeedOutboxAsync(unverified, "unverified-recipient");
        var calls = 0;
        var sender = new DelegatingSender((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.Equal(2, await CreateWorker(sender).ProcessBatchAsync());
        Assert.Equal(0, calls);
        Assert.Equal("INVALID_PAYLOAD", await ScalarAsync<string>(
            "SELECT last_error_code FROM store.commerce_email_outbox WHERE id=@id",
            new NpgsqlParameter("id", invalid)));
        Assert.Equal("RECIPIENT_INELIGIBLE", await ScalarAsync<string>(
            "SELECT last_error_code FROM store.commerce_email_outbox WHERE id=@id",
            new NpgsqlParameter("id", ineligible)));
        Assert.Equal(2, await ScalarAsync<int>("""
            SELECT count(*)::int FROM store.commerce_email_outbox
            WHERE id IN (@invalid,@ineligible) AND discarded_at IS NOT NULL
            """, new NpgsqlParameter("invalid", invalid), new NpgsqlParameter("ineligible", ineligible)));
    }

    [Fact]
    public async Task Cancellation_during_smtp_releases_lease_for_immediate_retry()
    {
        await ClearAsync();
        var user = await SeedUserAsync(verified: true);
        var outbox = await SeedOutboxAsync(user, "cancel-release");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingSender = new DelegatingSender(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var cancellation = new CancellationTokenSource();
        var processing = CreateWorker(blockingSender).ProcessBatchAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.True(await ScalarAsync<bool>("""
            SELECT sent_at IS NULL AND discarded_at IS NULL
               AND locked_at IS NULL AND locked_by IS NULL
            FROM store.commerce_email_outbox WHERE id=@id
            """, new NpgsqlParameter("id", outbox)));

        var sent = 0;
        Assert.Equal(1, await CreateWorker(new DelegatingSender((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.CompletedTask;
        })).ProcessBatchAsync());
        Assert.Equal(1, sent);
    }

    private CommerceEmailDeliveryWorker CreateWorker(ICommerceEmailSender sender) => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(),
        sender,
        Options.Create(new AuthEmailOptions { FrontendBaseUrl = "https://shop.example.test" }),
        TimeProvider.System,
        fixture.Services.GetRequiredService<ILogger<CommerceEmailDeliveryWorker>>());

    private async Task<Guid> SeedUserAsync(bool verified)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO store.user_accounts(
                id, role_id, email, password_hash, full_name, account_status, email_verified_at)
            VALUES(@id, (SELECT id FROM store.roles WHERE role_key='CUSTOMER'), @email,
                   'x', 'Commerce Email Customer', 'ACTIVE', CASE WHEN @verified THEN now() ELSE NULL END)
            """, new NpgsqlParameter("id", id),
            new NpgsqlParameter("email", $"commerce-email-{id:N}@example.invalid"),
            new NpgsqlParameter("verified", verified));
        return id;
    }

    private async Task<Guid> SeedOutboxAsync(Guid userId, string eventKey, string? payload = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO store.commerce_email_outbox(
                id, recipient_id, recipient_email, event_key, template_key, subject, payload)
            SELECT @id, id, email, @eventKey, 'WISHLIST_BACK_IN_STOCK', @subject, CAST(@payload AS jsonb)
            FROM store.user_accounts WHERE id=@userId
            """, new NpgsqlParameter("id", id), new NpgsqlParameter("userId", userId),
            new NpgsqlParameter("eventKey", eventKey), new NpgsqlParameter("subject", $"Alert {eventKey}"),
            new NpgsqlParameter("payload", payload ?? "{\"title\":\"Vợt Yonex <Pro>\",\"currentMinPrice\":1800000,\"actionPath\":\"/products/11111111-1111-1111-1111-111111111111\"}"));
        return id;
    }

    private Task ClearAsync() => ExecuteAsync("DELETE FROM store.commerce_email_outbox");

    private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected scalar."));
    }

    private sealed class DelegatingSender(Func<CommerceAlertEmail, CancellationToken, Task> callback) : ICommerceEmailSender
    {
        public Task SendAsync(CommerceAlertEmail email, CancellationToken ct = default) => callback(email, ct);
    }
}
