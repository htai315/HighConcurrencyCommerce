using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using HighConcurrencyCommerce.Application.ProductManagement;
using HighConcurrencyCommerce.Application.ProductRevisions;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.ProductRevisions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductRevisionTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "Revision secure pass 123!@#";

    [Fact]
    public async Task Revision_keeps_catalog_live_until_admin_approves_atomic_snapshot()
    {
        using var staff = await Role("STAFF"); using var admin = await Role("ADMIN");
        var productId = await PublishedProduct("Tên live ban đầu");

        using var created = await Send(staff, $"staff/products/{productId}/revisions");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var update = await Send(staff, $"staff/product-revisions/{revisionId}", new { title = "Tên sau duyệt", description = "Nội dung revision" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var spec = await Send(staff, $"staff/product-revisions/{revisionId}/specifications/trong_luong", new { displayName = "Trọng lượng", value = "4U", displayOrder = 1 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);

        Assert.Equal("Tên live ban đầu", await CatalogTitle(productId));
        using var submit = await Send(staff, $"staff/product-revisions/{revisionId}/submit");
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal("Tên live ban đầu", await CatalogTitle(productId));

        using var approve = await Send(admin, $"admin/product-revisions/{revisionId}/approve", new { note = "Đạt chuẩn nội dung" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("APPROVED", (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionStatus").GetString());
        Assert.Equal("Tên sau duyệt", await CatalogTitle(productId));

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT content_version,(SELECT count(*) FROM store.product_revision_specifications WHERE revision_id=@r),(SELECT count(*) FROM store.audit_logs WHERE action='PRODUCT_REVISION_APPROVED' AND entity_id=@r) FROM store.products WHERE id=@p", db);
        command.Parameters.AddWithValue("r", revisionId); command.Parameters.AddWithValue("p", productId);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(2L, reader.GetInt64(0)); Assert.Equal(1L, reader.GetInt64(1)); Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Concurrent_create_allows_exactly_one_open_revision_without_500()
    {
        using var first = await Role("STAFF"); using var second = await Role("STAFF"); var productId = await PublishedProduct("Concurrent revision");
        var responses = await Task.WhenAll(Send(first, $"staff/products/{productId}/revisions"), Send(second, $"staff/products/{productId}/revisions"));
        try
        {
            Assert.Equal(new[] { 201, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray());
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Revision_rbac_and_terminal_state_are_enforced()
    {
        using var staff = await Role("STAFF"); using var customer = await Role("CUSTOMER"); using var admin = await Role("ADMIN"); var productId = await PublishedProduct("RBAC revision");
        using var forbidden = await Send(customer, $"staff/products/{productId}/revisions"); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var created = await Send(staff, $"staff/products/{productId}/revisions"); var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var prematureApprove = await Send(admin, $"admin/product-revisions/{id}/approve", new { }); Assert.Equal(HttpStatusCode.Conflict, prematureApprove.StatusCode);
        using var submit = await Send(staff, $"staff/product-revisions/{id}/submit"); Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        using var frozen = await Send(staff, $"staff/product-revisions/{id}", new { title = "Không được sửa" }, HttpMethod.Patch); Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
        using var reject = await Send(admin, $"admin/product-revisions/{id}/reject", new { reason = "Thiếu thông tin kỹ thuật" }); Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
    }

    [Fact]
    public async Task Concurrent_admin_approval_is_exactly_once()
    {
        using var staff = await Role("STAFF"); using var firstAdmin = await Role("ADMIN"); using var secondAdmin = await Role("ADMIN"); var productId = await PublishedProduct("Approve once");
        using var created = await Send(staff, $"staff/products/{productId}/revisions"); var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var updated = await Send(staff, $"staff/product-revisions/{revisionId}", new { title = "Approved once title" }, HttpMethod.Patch); Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var submitted = await Send(staff, $"staff/product-revisions/{revisionId}/submit"); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var responses = await Task.WhenAll(Send(firstAdmin, $"admin/product-revisions/{revisionId}/approve", new { }), Send(secondAdmin, $"admin/product-revisions/{revisionId}/approve", new { }));
        try { Assert.Equal(new[] { 200, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray()); }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = await fixture.OpenDatabaseAsync(); await using var command = new NpgsqlCommand("SELECT content_version,(SELECT count(*) FROM store.audit_logs WHERE action='PRODUCT_REVISION_APPROVED' AND entity_id=@r) FROM store.products WHERE id=@p", db); command.Parameters.AddWithValue("r", revisionId); command.Parameters.AddWithValue("p", productId); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(2L, reader.GetInt64(0)); Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Admin_review_returns_structured_live_to_proposed_diff_and_staleness_guard()
    {
        using var staff = await Role("STAFF"); using var admin = await Role("ADMIN"); var productId = await PublishedProduct("Live review title");
        using var created = await Send(staff, $"staff/products/{productId}/revisions"); var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var update = await Send(staff, $"staff/product-revisions/{revisionId}", new { title = "Proposed review title", description = "Proposed description" }, HttpMethod.Patch); Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var spec = await Send(staff, $"staff/product-revisions/{revisionId}/specifications/flex", new { displayName = "Độ cứng", value = "Trung bình", displayOrder = 1 }, HttpMethod.Put); Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        using var submit = await Send(staff, $"staff/product-revisions/{revisionId}/submit"); Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        using var review = await admin.GetAsync($"/api/v1/admin/product-revisions/{revisionId}/review");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var payload = await review.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("canApprove").GetBoolean());
        Assert.False(payload.GetProperty("isStale").GetBoolean());
        Assert.Contains(payload.GetProperty("content").EnumerateArray(), x => x.GetProperty("field").GetString() == "title" && x.GetProperty("changed").GetBoolean());
        Assert.Contains(payload.GetProperty("specifications").EnumerateArray(), x => x.GetProperty("key").GetString() == "flex" && x.GetProperty("changeType").GetString() == "ADDED");
    }

    [Fact]
    public async Task Reject_enqueues_new_revision_media_cleanup_in_same_transaction()
    {
        using var staff = await Role("STAFF"); using var admin = await Role("ADMIN"); var productId = await PublishedProduct("Cleanup revision");
        using var created = await Send(staff, $"staff/products/{productId}/revisions"); var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var publicPath = $"/api/v1/media/products/{productId:N}/{Guid.NewGuid():N}.png";
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var insert = new NpgsqlCommand("INSERT INTO store.product_revision_images(id,revision_id,file_key,image_status,display_order,is_new_upload) VALUES(gen_random_uuid(),@r,@path,'ACTIVE',0,true)", db))
        { insert.Parameters.AddWithValue("r", revisionId); insert.Parameters.AddWithValue("path", publicPath); await insert.ExecuteNonQueryAsync(); }
        using var submit = await Send(staff, $"staff/product-revisions/{revisionId}/submit"); Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        using var reject = await Send(admin, $"admin/product-revisions/{revisionId}/reject", new { reason = "Ảnh chưa đạt tiêu chuẩn" }); Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        await using var verify = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT cleanup_reason,job_status,storage_key FROM store.product_media_cleanup_jobs WHERE revision_id=@r", verify); command.Parameters.AddWithValue("r", revisionId);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal("REVISION_REJECTED", reader.GetString(0)); Assert.Equal("PENDING", reader.GetString(1)); Assert.EndsWith(".png", reader.GetString(2));
    }

    [Theory]
    [InlineData(ProductRevisionApprovalStage.AfterScalarMutation)]
    [InlineData(ProductRevisionApprovalStage.AfterImageMutation)]
    [InlineData(ProductRevisionApprovalStage.AfterSpecificationMutation)]
    [InlineData(ProductRevisionApprovalStage.AfterSaveBeforeCommit)]
    public async Task Approval_fault_at_every_checkpoint_rolls_back_the_entire_snapshot(ProductRevisionApprovalStage stage)
    {
        using var staff = await Role("STAFF"); var productId = await PublishedProduct("Atomic live title");
        using var created = await Send(staff, $"staff/products/{productId}/revisions"); var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var update = await Send(staff, $"staff/product-revisions/{revisionId}", new { title = "Must roll back", description = "Not visible" }, HttpMethod.Patch); Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var spec = await Send(staff, $"staff/product-revisions/{revisionId}/specifications/fault_spec", new { displayName = "Fault", value = "Injected", displayOrder = 1 }, HttpMethod.Put); Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        using var submit = await Send(staff, $"staff/product-revisions/{revisionId}/submit"); Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var adminId = await UserId("ADMIN");
        using (var scope = fixture.Services.CreateScope())
        {
            var service = new PostgresProductRevisionService(
                scope.ServiceProvider.GetRequiredService<StoreDbContext>(),
                scope.ServiceProvider.GetRequiredService<IProductMediaStorage>(),
                new ThrowingCheckpoint(stage),
                fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                scope.ServiceProvider.GetRequiredService<ILogger<PostgresProductRevisionService>>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(adminId, revisionId, new RevisionDecisionRequest(), "fault-test", default));
        }

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT p.title,p.content_version,r.revision_status,(SELECT count(*) FROM store.product_specifications WHERE product_id=@p),(SELECT count(*) FROM store.audit_logs WHERE action='PRODUCT_REVISION_APPROVED' AND entity_id=@r) FROM store.products p JOIN store.product_revisions r ON r.product_id=p.id WHERE p.id=@p AND r.id=@r", db);
        command.Parameters.AddWithValue("p", productId); command.Parameters.AddWithValue("r", revisionId);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal("Atomic live title", reader.GetString(0)); Assert.Equal(1L, reader.GetInt64(1)); Assert.Equal("PENDING_REVIEW", reader.GetString(2)); Assert.Equal(0L, reader.GetInt64(3)); Assert.Equal(0L, reader.GetInt64(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Media_cleanup_worker_persists_success_or_retry_without_losing_the_job(bool failDelete)
    {
        var productId = await PublishedProduct("Cleanup worker"); var jobId = Guid.NewGuid();
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var command = new NpgsqlCommand("DELETE FROM store.product_media_cleanup_jobs; INSERT INTO store.product_media_cleanup_jobs(id,product_id,storage_key,cleanup_reason,job_status,next_attempt_at) VALUES(@id,@p,@key,'UPLOAD_COMPENSATION','PENDING',now()-interval '1 minute')", db))
        { command.Parameters.AddWithValue("id", jobId); command.Parameters.AddWithValue("p", productId); command.Parameters.AddWithValue("key", $"products/{productId:N}/{Guid.NewGuid():N}.png"); await command.ExecuteNonQueryAsync(); }
        var storage = new CleanupStorage(failDelete);
        var worker = new ProductMediaCleanupWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(), storage, TimeProvider.System, fixture.Services.GetRequiredService<ILogger<ProductMediaCleanupWorker>>());
        Assert.Equal(1, await worker.ProcessBatchAsync());

        await using var verify = await fixture.OpenDatabaseAsync();
        await using var check = new NpgsqlCommand("SELECT job_status,attempt_count,locked_at IS NULL,locked_by IS NULL,completed_at IS NOT NULL,last_error_code FROM store.product_media_cleanup_jobs WHERE id=@id", verify); check.Parameters.AddWithValue("id", jobId);
        await using var reader = await check.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal(failDelete ? "PENDING" : "COMPLETED", reader.GetString(0)); Assert.Equal(failDelete ? 1 : 0, reader.GetInt32(1)); Assert.True(reader.GetBoolean(2)); Assert.True(reader.GetBoolean(3)); Assert.Equal(!failDelete, reader.GetBoolean(4));
        if (failDelete) Assert.Equal(nameof(IOException), reader.GetString(5)); else Assert.True(reader.IsDBNull(5));
    }

    [Fact]
    public async Task Concurrent_approve_and_suspend_are_serialized_and_preserve_both_decisions()
    {
        using var staff = await Role("STAFF"); using var revisionAdmin = await Role("ADMIN"); using var lifecycleAdmin = await Role("ADMIN"); var productId = await PublishedProduct("Live before concurrent decisions");
        using var created = await Send(staff, $"staff/products/{productId}/revisions"); var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var update = await Send(staff, $"staff/product-revisions/{revisionId}", new { title = "Approved concurrent content" }, HttpMethod.Patch); Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var submit = await Send(staff, $"staff/product-revisions/{revisionId}/submit"); Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var responses = await Task.WhenAll(
            Send(revisionAdmin, $"admin/product-revisions/{revisionId}/approve", new { note = "Approve concurrently" }),
            Send(lifecycleAdmin, $"admin/products/{productId}/suspend", new { reason = "Tạm dừng đồng thời để kiểm tra" }));
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }

        await using var db = await fixture.OpenDatabaseAsync(); await using var command = new NpgsqlCommand("SELECT p.title,p.publication_status,r.revision_status,p.content_version FROM store.products p JOIN store.product_revisions r ON r.product_id=p.id WHERE p.id=@p AND r.id=@r", db); command.Parameters.AddWithValue("p", productId); command.Parameters.AddWithValue("r", revisionId);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal("Approved concurrent content", reader.GetString(0)); Assert.Equal("SUSPENDED", reader.GetString(1)); Assert.Equal("APPROVED", reader.GetString(2)); Assert.Equal(2L, reader.GetInt64(3));
    }

    [Fact]
    public async Task Concurrent_cleanup_workers_claim_disjoint_batches_without_double_delete()
    {
        var productId = await PublishedProduct("Concurrent cleanup workers");
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var command = new NpgsqlCommand("DELETE FROM store.product_media_cleanup_jobs; INSERT INTO store.product_media_cleanup_jobs(product_id,storage_key,cleanup_reason,job_status,next_attempt_at) SELECT @p,'cleanup/' || n || '.png','UPLOAD_COMPENSATION','PENDING',now()-interval '1 minute' FROM generate_series(1,20) AS n", db))
        { command.Parameters.AddWithValue("p", productId); await command.ExecuteNonQueryAsync(); }
        var storage = new CleanupStorage(false);
        var scopeFactory = fixture.Services.GetRequiredService<IServiceScopeFactory>(); var logger = fixture.Services.GetRequiredService<ILogger<ProductMediaCleanupWorker>>();
        var first = new ProductMediaCleanupWorker(scopeFactory, storage, TimeProvider.System, logger); var second = new ProductMediaCleanupWorker(scopeFactory, storage, TimeProvider.System, logger);
        var handled = await Task.WhenAll(first.ProcessBatchAsync(), second.ProcessBatchAsync());
        Assert.Equal(20, handled.Sum()); Assert.Equal(20, storage.Deleted.Count); Assert.Equal(20, storage.Deleted.Distinct().Count());
        await using var verify = await fixture.OpenDatabaseAsync(); await using var check = new NpgsqlCommand("SELECT count(*) FROM store.product_media_cleanup_jobs WHERE job_status='COMPLETED'", verify); Assert.Equal(20L, (long)(await check.ExecuteScalarAsync())!);
    }

    private async Task<Guid> PublishedProduct(string title)
    {
        await using var db = await fixture.OpenDatabaseAsync(); var productId = Guid.NewGuid(); var skuId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,description,publication_status,created_by,published_by,published_at,content_version) VALUES(@p,@t,'Live','PUBLISHED',(SELECT id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key='STAFF') LIMIT 1),(SELECT id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') LIMIT 1),now(),1);
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) VALUES(@s,@p,@code,'4U/G5',1000000,'ACTIVE');
            """, db);
        command.Parameters.AddWithValue("p", productId); command.Parameters.AddWithValue("s", skuId); command.Parameters.AddWithValue("t", title); command.Parameters.AddWithValue("code", $"REV-{Guid.NewGuid():N}"); await command.ExecuteNonQueryAsync(); return productId;
    }

    private async Task<string> CatalogTitle(Guid productId)
    {
        using var response = await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId}"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
    }

    private async Task<Guid> UserId(string role)
    {
        await using var db = await fixture.OpenDatabaseAsync(); await using var command = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key=@role) LIMIT 1", db); command.Parameters.AddWithValue("role", role); return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<HttpClient> Role(string role)
    {
        var client = fixture.CreateClient(); var email = $"revision_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = $"{role} Revision" }); Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync(); await using var update = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key=@role),email_verified_at=now() WHERE lower(email)=lower(@email)", db); update.Parameters.AddWithValue("role", role); update.Parameters.AddWithValue("email", email); await update.ExecuteNonQueryAsync();
        using var login = await Send(client, "auth/login", new { email, password = Password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); return client;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path); if (body is not null) request.Content = JsonContent.Create(body); request.Headers.Add("X-CSRF-TOKEN", await Csrf(client)); return await client.SendAsync(request);
    }
    private static async Task<string> Csrf(HttpClient client) { using var response = await client.GetAsync("/api/v1/auth/csrf"); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!; }

    private sealed class ThrowingCheckpoint(ProductRevisionApprovalStage target) : IProductRevisionApprovalCheckpoint
    {
        public Task ReachAsync(ProductRevisionApprovalStage stage, CancellationToken ct = default) =>
            stage == target ? Task.FromException(new InvalidOperationException($"Injected failure at {stage}")) : Task.CompletedTask;
    }

    private sealed class CleanupStorage(bool failDelete) : IProductMediaStorage
    {
        public ConcurrentBag<string> Deleted { get; } = [];
        public Task DeleteAsync(string storageKey, CancellationToken ct = default) { if (failDelete) return Task.FromException(new IOException("Injected storage failure")); Deleted.Add(storageKey); return Task.CompletedTask; }
        public Task<OpenProductMedia?> OpenReadAsync(string storageKey, CancellationToken ct = default) => Task.FromResult<OpenProductMedia?>(null);
        public Task<StoredProductMedia> StoreAsync(Guid productId, Stream content, string originalFileName, string declaredContentType, long length, CancellationToken ct = default) => throw new NotSupportedException();
        public bool TryGetStorageKey(string publicPath, out string storageKey) { storageKey = string.Empty; return false; }
    }
}
