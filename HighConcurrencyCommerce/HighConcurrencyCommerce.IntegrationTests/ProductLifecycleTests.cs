using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductLifecycleTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "Lifecycle secure pass 123!@#";

    [Fact]
    public async Task Admin_can_suspend_resume_and_archive_with_catalog_and_audit_consistency()
    {
        using var admin = await Role("ADMIN");
        var (productId, _) = await PublishedProduct("Lifecycle catalog");
        Assert.Equal(HttpStatusCode.OK, (await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId}")).StatusCode);

        using var suspend = await Send(admin, $"admin/products/{productId}/suspend", new { reason = "Kiểm tra chất lượng lô hàng" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        Assert.Equal("SUSPENDED", (await suspend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicationStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId}")).StatusCode);

        using var resume = await Send(admin, $"admin/products/{productId}/resume", new { });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId}")).StatusCode);

        using var suspendAgain = await Send(admin, $"admin/products/{productId}/suspend", new { reason = "Ngừng kinh doanh phiên bản cũ" });
        Assert.Equal(HttpStatusCode.OK, suspendAgain.StatusCode);
        using var archive = await Send(admin, $"admin/products/{productId}/archive", new { reason = "Sản phẩm đã ngừng kinh doanh" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        using var cannotResume = await Send(admin, $"admin/products/{productId}/resume", new { });
        Assert.Equal(HttpStatusCode.Conflict, cannotResume.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT publication_status,status_reason,(SELECT count(*) FROM store.audit_logs WHERE entity_id=@p AND action IN ('PRODUCT_SUSPENDED','PRODUCT_RESUMED','PRODUCT_ARCHIVED')) FROM store.products WHERE id=@p", db);
        command.Parameters.AddWithValue("p", productId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ARCHIVED", reader.GetString(0));
        Assert.Equal("Sản phẩm đã ngừng kinh doanh", reader.GetString(1));
        Assert.Equal(4L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Lifecycle_enforces_rbac_validation_and_resume_requirements()
    {
        using var customer = await Role("CUSTOMER"); using var staff = await Role("STAFF"); using var admin = await Role("ADMIN");
        var (productId, skuId) = await PublishedProduct("Lifecycle RBAC");
        using var customerForbidden = await Send(customer, $"admin/products/{productId}/suspend", new { reason = "Không đủ quyền thao tác" });
        using var staffForbidden = await Send(staff, $"admin/products/{productId}/suspend", new { reason = "Không đủ quyền thao tác" });
        Assert.Equal(HttpStatusCode.Forbidden, customerForbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, staffForbidden.StatusCode);
        using var invalidReason = await Send(admin, $"admin/products/{productId}/suspend", new { reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidReason.StatusCode);
        using var suspended = await Send(admin, $"admin/products/{productId}/suspend", new { reason = "Tạm dừng để kiểm tra" });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var update = new NpgsqlCommand("UPDATE store.product_skus SET regular_price=0 WHERE id=@s", db))
        { update.Parameters.AddWithValue("s", skuId); await update.ExecuteNonQueryAsync(); }
        using var rejectedResume = await Send(admin, $"admin/products/{productId}/resume", new { });
        Assert.Equal(HttpStatusCode.Conflict, rejectedResume.StatusCode);
        Assert.Equal("PRODUCT_RESUME_REQUIREMENTS_NOT_MET", await ErrorCode(rejectedResume));
    }

    [Fact]
    public async Task Suspended_product_keeps_cart_placeholder_and_supports_revision_without_resuming()
    {
        using var customer = await Role("CUSTOMER"); using var staff = await Role("STAFF"); using var admin = await Role("ADMIN");
        var (productId, skuId) = await PublishedProduct("Lifecycle cart and revision");
        using var add = await Send(customer, $"cart/items/{skuId}", new { quantity = 1 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        using var suspend = await Send(admin, $"admin/products/{productId}/suspend", new { reason = "Tạm dừng để cập nhật nội dung" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        using var cart = await customer.GetAsync("/api/v1/cart");
        var cartJson = await cart.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PRODUCT_SUSPENDED", cartJson.GetProperty("items")[0].GetProperty("availabilityCode").GetString());
        Assert.False(cartJson.GetProperty("canCheckout").GetBoolean());
        using var addAgain = await Send(customer, $"cart/items/{skuId}", new { quantity = 2 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.NotFound, addAgain.StatusCode);

        using var created = await Send(staff, $"staff/products/{productId}/revisions", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var revisionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var updated = await Send(staff, $"staff/product-revisions/{revisionId}", new { title = "Nội dung mới khi tạm dừng" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var submit = await Send(staff, $"staff/product-revisions/{revisionId}/submit", new { });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        using var approve = await Send(admin, $"admin/product-revisions/{revisionId}/approve", new { note = "Đạt" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT title,publication_status FROM store.products WHERE id=@p", db);
        command.Parameters.AddWithValue("p", productId);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("Nội dung mới khi tạm dừng", reader.GetString(0));
        Assert.Equal("SUSPENDED", reader.GetString(1));
    }

    [Fact]
    public async Task Archive_is_blocked_by_open_revision_and_effective_flash_sale()
    {
        using var staff = await Role("STAFF"); using var admin = await Role("ADMIN");
        var (productId, skuId) = await PublishedProduct("Lifecycle archive guards");
        using var suspend = await Send(admin, $"admin/products/{productId}/suspend", new { reason = "Chuẩn bị lưu trữ sản phẩm" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        using var revision = await Send(staff, $"staff/products/{productId}/revisions", new { });
        var revisionId = (await revision.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var blockedByRevision = await Send(admin, $"admin/products/{productId}/archive", new { reason = "Sản phẩm đã ngừng kinh doanh" });
        Assert.Equal(HttpStatusCode.Conflict, blockedByRevision.StatusCode);
        Assert.Equal("PRODUCT_ARCHIVE_OPEN_REVISION", await ErrorCode(blockedByRevision));
        using var cancel = await Send(staff, $"staff/product-revisions/{revisionId}/cancel", new { note = "Không tiếp tục" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var insert = new NpgsqlCommand("INSERT INTO store.flash_sale_campaigns(id,name,starts_at,ends_at,campaign_status,created_by) VALUES(@c,'Lifecycle campaign',now()-interval '1 minute',now()+interval '1 hour','ACTIVE',@a); INSERT INTO store.flash_sale_offers(id,campaign_id,product_sku_id,sale_price,quota_total,per_customer_limit) VALUES(gen_random_uuid(),@c,@s,900000,10,1)", db))
        {
            insert.Parameters.AddWithValue("c", Guid.NewGuid()); insert.Parameters.AddWithValue("s", skuId); insert.Parameters.AddWithValue("a", await UserId("ADMIN")); await insert.ExecuteNonQueryAsync();
        }
        using var blockedByOffer = await Send(admin, $"admin/products/{productId}/archive", new { reason = "Sản phẩm đã ngừng kinh doanh" });
        Assert.Equal(HttpStatusCode.Conflict, blockedByOffer.StatusCode);
        Assert.Equal("PRODUCT_ARCHIVE_ACTIVE_FLASH_SALE", await ErrorCode(blockedByOffer));
    }

    [Fact]
    public async Task Concurrent_resume_and_archive_allows_exactly_one_transition_without_500()
    {
        using var first = await Role("ADMIN"); using var second = await Role("ADMIN");
        var (productId, _) = await PublishedProduct("Lifecycle concurrent transition");
        using var suspended = await Send(first, $"admin/products/{productId}/suspend", new { reason = "Chuẩn bị quyết định đồng thời" });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        var responses = await Task.WhenAll(
            Send(first, $"admin/products/{productId}/resume", new { }),
            Send(second, $"admin/products/{productId}/archive", new { reason = "Lưu trữ do ngừng kinh doanh" }));
        try
        {
            Assert.Equal(new[] { 200, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray());
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
        }
        finally { foreach (var response in responses) response.Dispose(); }

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM store.audit_logs WHERE entity_id=@p AND action IN ('PRODUCT_RESUMED','PRODUCT_ARCHIVED')", db);
        command.Parameters.AddWithValue("p", productId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Concurrent_suspend_and_checkout_has_only_linearizable_outcomes_without_leaks()
    {
        using var customer = await Role("CUSTOMER"); using var admin = await Role("ADMIN");
        var (productId, skuId) = await PublishedProduct("Lifecycle checkout race");
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var inventory = new NpgsqlCommand("INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision) VALUES(gen_random_uuid(),@s,5,5,0,0,0)", db))
        { inventory.Parameters.AddWithValue("s", skuId); await inventory.ExecuteNonQueryAsync(); }
        using var addressResponse = await Send(customer, "account/addresses", new { recipientName = "Khách lifecycle", phone = "0901234567", addressLine = "12 Sân Cầu", ward = "Phường 1", province = "TP Hồ Chí Minh", isDefault = true });
        Assert.Equal(HttpStatusCode.Created, addressResponse.StatusCode);
        var addressId = (await addressResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var checkoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkouts")
        {
            Content = JsonContent.Create(new { shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee, paymentMethod = "COD", lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } } })
        };
        checkoutRequest.Headers.Add("X-CSRF-TOKEN", await Csrf(customer));
        checkoutRequest.Headers.Add("Idempotency-Key", "lifecycle-race-" + Guid.NewGuid().ToString("N"));
        var checkoutTask = customer.SendAsync(checkoutRequest);
        var suspendTask = Send(admin, $"admin/products/{productId}/suspend", new { reason = "Ngừng bán đồng thời checkout" });
        var responses = await Task.WhenAll(checkoutTask, suspendTask);
        try
        {
            var checkout = responses[0]; var suspend = responses[1];
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Contains(checkout.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);

            await using var db = await fixture.OpenDatabaseAsync();
            await using var command = new NpgsqlCommand("SELECT p.publication_status,b.available_qty,b.reserved_qty,(SELECT count(*) FROM store.sales_order_items WHERE product_sku_id=@s) FROM store.products p JOIN store.product_skus s ON s.product_id=p.id JOIN store.inventory_balances b ON b.product_sku_id=s.id WHERE p.id=@p AND s.id=@s", db);
            command.Parameters.AddWithValue("p", productId); command.Parameters.AddWithValue("s", skuId);
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal("SUSPENDED", reader.GetString(0));
            if (checkout.StatusCode == HttpStatusCode.Created) { Assert.Equal(4, reader.GetInt32(1)); Assert.Equal(1, reader.GetInt32(2)); Assert.Equal(1L, reader.GetInt64(3)); }
            else { Assert.Equal(5, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(0L, reader.GetInt64(3)); }
        }
        finally { checkoutRequest.Dispose(); foreach (var response in responses) response.Dispose(); }
    }

    private async Task<(Guid ProductId, Guid SkuId)> PublishedProduct(string title)
    {
        await using var db = await fixture.OpenDatabaseAsync(); var productId = Guid.NewGuid(); var skuId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at,content_version) VALUES(@p,@t,'PUBLISHED',(SELECT id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') LIMIT 1),(SELECT id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN') LIMIT 1),now(),1); INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) VALUES(@s,@p,@code,'4U/G5',1000000,'ACTIVE')", db);
        command.Parameters.AddWithValue("p", productId); command.Parameters.AddWithValue("s", skuId); command.Parameters.AddWithValue("t", title); command.Parameters.AddWithValue("code", $"LIFE-{Guid.NewGuid():N}"); await command.ExecuteNonQueryAsync(); return (productId, skuId);
    }

    private async Task<Guid> UserId(string role)
    {
        await using var db = await fixture.OpenDatabaseAsync(); await using var command = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE role_id=(SELECT id FROM store.roles WHERE role_key=@r) LIMIT 1", db); command.Parameters.AddWithValue("r", role); return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<HttpClient> Role(string role)
    {
        var client = fixture.CreateClient(); var email = $"lifecycle_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = $"{role} Lifecycle" }); Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync(); await using var update = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key=@role),email_verified_at=now() WHERE lower(email)=lower(@email)", db); update.Parameters.AddWithValue("role", role); update.Parameters.AddWithValue("email", email); await update.ExecuteNonQueryAsync();
        using var login = await Send(client, "auth/login", new { email, password = Password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); return client;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path); if (body is not null) request.Content = JsonContent.Create(body); request.Headers.Add("X-CSRF-TOKEN", await Csrf(client)); return await client.SendAsync(request);
    }
    private static async Task<string> Csrf(HttpClient client) { using var response = await client.GetAsync("/api/v1/auth/csrf"); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!; }
    private static async Task<string?> ErrorCode(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
