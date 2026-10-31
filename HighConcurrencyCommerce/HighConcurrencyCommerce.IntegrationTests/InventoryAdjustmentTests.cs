using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class InventoryAdjustmentTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure adjustment pass 123!";
    private static string Email(string prefix) => $"{prefix}_{Guid.NewGuid():N}@example.invalid";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleClient(string roleKey, string prefix)
    {
        var email = Email(prefix);
        var userId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @role", db);
        roleCmd.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status) " +
            "VALUES (@id, @roleId, @email, @hash, @name, 'ACTIVE')", db);
        insert.Parameters.AddWithValue("id", userId);
        insert.Parameters.AddWithValue("roleId", roleId);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("name", $"{roleKey} Adjustment");
        await insert.ExecuteNonQueryAsync();

        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, userId);
    }

    private async Task<Guid> CreateSkuWithBalance(Guid createdBy, int available, int reserved = 0, int allocated = 0)
    {
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var physical = available + reserved + allocated;
        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO store.products (id, title, publication_status, created_by) VALUES (@product, @title, 'DRAFT', @actor); " +
            "INSERT INTO store.product_skus (id, product_id, sku_code, option_label, regular_price, sku_status) " +
            "VALUES (@sku, @product, @code, 'Adjustment test', 1000, 'ACTIVE'); " +
            "INSERT INTO store.inventory_balances (id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, revision) " +
            "VALUES (gen_random_uuid(), @sku, @physical, @available, @reserved, @allocated, 0);", db);
        cmd.Parameters.AddWithValue("product", productId);
        cmd.Parameters.AddWithValue("sku", skuId);
        cmd.Parameters.AddWithValue("actor", createdBy);
        cmd.Parameters.AddWithValue("title", "Adjustment product " + productId.ToString("N")[..8]);
        cmd.Parameters.AddWithValue("code", "ADJ-" + skuId.ToString("N")[..12].ToUpperInvariant());
        cmd.Parameters.AddWithValue("physical", physical);
        cmd.Parameters.AddWithValue("available", available);
        cmd.Parameters.AddWithValue("reserved", reserved);
        cmd.Parameters.AddWithValue("allocated", allocated);
        await cmd.ExecuteNonQueryAsync();
        return skuId;
    }

    [Fact]
    public async Task Adjustment_endpoints_enforce_strict_staff_admin_separation()
    {
        using var guest = fixture.CreateClient();
        var (staff, _) = await CreateRoleClient("STAFF", "adj_rbac_staff");
        var (admin, _) = await CreateRoleClient("ADMIN", "adj_rbac_admin");
        var (customer, _) = await CreateRoleClient("CUSTOMER", "adj_rbac_customer");
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/v1/admin/inventory/adjustment-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staff.GetAsync("/api/v1/admin/inventory/adjustment-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync("/api/v1/admin/inventory/adjustment-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync("/api/v1/admin/inventory/adjustment-requests")).StatusCode);

        using var adminCreate = await Send(admin, "staff/inventory/adjustment-requests", new { productSkuId = id, quantity = 1, reason = "No" });
        Assert.Equal(HttpStatusCode.Forbidden, adminCreate.StatusCode);
        using var customerCreate = await Send(customer, "staff/inventory/adjustment-requests", new { productSkuId = id, quantity = 1, reason = "No" });
        Assert.Equal(HttpStatusCode.Forbidden, customerCreate.StatusCode);
    }

    [Fact]
    public async Task Staff_creates_pending_negative_adjustment_without_changing_balance()
    {
        var (staff, staffId) = await CreateRoleClient("STAFF", "adj_create");
        var skuId = await CreateSkuWithBalance(staffId, available: 20, reserved: 3, allocated: 2);

        using var response = await Send(staff, "staff/inventory/adjustment-requests",
            new { productSkuId = skuId, quantity = 7, reason = "Hàng hỏng khi kiểm kê" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(-7, body.GetProperty("deltaQty").GetInt32());
        Assert.Equal("PENDING", body.GetProperty("reviewStatus").GetString());
        Assert.Equal(staffId, body.GetProperty("requestedBy").GetGuid());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var balanceCmd = new NpgsqlCommand(
            "SELECT physical_qty, available_qty, reserved_qty, allocated_qty FROM store.inventory_balances WHERE product_sku_id = @sku", db);
        balanceCmd.Parameters.AddWithValue("sku", skuId);
        await using var reader = await balanceCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(25, reader.GetInt32(0));
        Assert.Equal(20, reader.GetInt32(1));
        Assert.Equal(3, reader.GetInt32(2));
        Assert.Equal(2, reader.GetInt32(3));
    }

    [Fact]
    public async Task Admin_approval_applies_available_decrease_movement_and_audit_atomically()
    {
        var (staff, staffId) = await CreateRoleClient("STAFF", "adj_apply_staff");
        var (admin, adminId) = await CreateRoleClient("ADMIN", "adj_apply_admin");
        var skuId = await CreateSkuWithBalance(staffId, available: 12, reserved: 5, allocated: 3);
        using var create = await Send(staff, "staff/inventory/adjustment-requests",
            new { productSkuId = skuId, quantity = 7, reason = "Loại bỏ hàng lỗi" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = created.GetProperty("id").GetGuid();

        using var decision = await Send(admin, $"admin/inventory/adjustment-requests/{requestId}/decision", new { decision = "APPROVE" });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        var result = await decision.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("APPLIED", result.GetProperty("request").GetProperty("reviewStatus").GetString());
        Assert.Equal(adminId, result.GetProperty("request").GetProperty("reviewedBy").GetGuid());
        Assert.Equal(13, result.GetProperty("balance").GetProperty("physicalQty").GetInt32());
        Assert.Equal(5, result.GetProperty("balance").GetProperty("availableQty").GetInt32());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var verify = new NpgsqlCommand(
            "SELECT b.physical_qty, b.available_qty, b.reserved_qty, b.allocated_qty, b.revision, " +
            "(SELECT COUNT(*) FROM store.inventory_movements m WHERE m.source_type = 'INVENTORY_ADJUSTMENT' AND m.source_id = @request), " +
            "(SELECT COUNT(*) FROM store.audit_logs a WHERE a.action = 'INVENTORY_ADJUSTMENT_APPLIED' AND a.entity_id = @request) " +
            "FROM store.inventory_balances b WHERE b.product_sku_id = @sku", db);
        verify.Parameters.AddWithValue("request", requestId);
        verify.Parameters.AddWithValue("sku", skuId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(13, reader.GetInt32(0));
        Assert.Equal(5, reader.GetInt32(1));
        Assert.Equal(5, reader.GetInt32(2));
        Assert.Equal(3, reader.GetInt32(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
    }

    [Fact]
    public async Task Admin_rejection_is_terminal_and_does_not_change_inventory()
    {
        var (staff, staffId) = await CreateRoleClient("STAFF", "adj_reject_staff");
        var (admin, _) = await CreateRoleClient("ADMIN", "adj_reject_admin");
        var skuId = await CreateSkuWithBalance(staffId, available: 9);
        using var create = await Send(staff, "staff/inventory/adjustment-requests",
            new { productSkuId = skuId, quantity = 4, reason = "Chênh lệch cần xác minh" });
        var requestId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var reject = await Send(admin, $"admin/inventory/adjustment-requests/{requestId}/decision", new { decision = "REJECT" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var body = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REJECTED", body.GetProperty("request").GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("balance").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("movementId").ValueKind);

        using var secondDecision = await Send(admin, $"admin/inventory/adjustment-requests/{requestId}/decision", new { decision = "APPROVE" });
        Assert.Equal(HttpStatusCode.Conflict, secondDecision.StatusCode);
        var error = await secondDecision.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ALREADY_DECIDED", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Approval_rechecks_available_stock_and_keeps_request_pending_when_insufficient()
    {
        var (staff, staffId) = await CreateRoleClient("STAFF", "adj_short_staff");
        var (admin, _) = await CreateRoleClient("ADMIN", "adj_short_admin");
        var skuId = await CreateSkuWithBalance(staffId, available: 10);
        using var create = await Send(staff, "staff/inventory/adjustment-requests",
            new { productSkuId = skuId, quantity = 8, reason = "Đề nghị giảm" });
        var requestId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var reserve = new NpgsqlCommand(
            "UPDATE store.inventory_balances SET available_qty = 3, reserved_qty = 7 WHERE product_sku_id = @sku", db))
        {
            reserve.Parameters.AddWithValue("sku", skuId);
            await reserve.ExecuteNonQueryAsync();
        }

        using var approve = await Send(admin, $"admin/inventory/adjustment-requests/{requestId}/decision", new { decision = "APPROVE" });
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        var error = await approve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INSUFFICIENT_AVAILABLE", error.GetProperty("code").GetString());

        await using var verifyDb = await fixture.OpenDatabaseAsync();
        await using var statusCmd = new NpgsqlCommand("SELECT review_status FROM store.inventory_adjustment_requests WHERE id = @id", verifyDb);
        statusCmd.Parameters.AddWithValue("id", requestId);
        Assert.Equal("PENDING", (string)(await statusCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Concurrent_approval_is_applied_exactly_once_and_serializes_with_stock_in()
    {
        var (staff, staffId) = await CreateRoleClient("STAFF", "adj_race_staff");
        var (admin1, _) = await CreateRoleClient("ADMIN", "adj_race_admin1");
        var (admin2, _) = await CreateRoleClient("ADMIN", "adj_race_admin2");
        var skuId = await CreateSkuWithBalance(staffId, available: 20);
        using var create = await Send(staff, "staff/inventory/adjustment-requests",
            new { productSkuId = skuId, quantity = 6, reason = "Race approval" });
        var requestId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var approve1 = Send(admin1, $"admin/inventory/adjustment-requests/{requestId}/decision", new { decision = "APPROVE" });
        var approve2 = Send(admin2, $"admin/inventory/adjustment-requests/{requestId}/decision", new { decision = "APPROVE" });
        var stockInTask = Send(staff, "staff/inventory/stock-in",
            new { productSkuId = skuId, quantity = 5, sourceType = "ADJUSTMENT_RACE_TEST", sourceId = Guid.NewGuid(), note = "Concurrent decision" });
        var results = await Task.WhenAll(approve1, approve2);
        using var stockIn = await stockInTask;
        using var first = results[0];
        using var second = results[1];
        Assert.Contains(HttpStatusCode.OK, results.Select(r => r.StatusCode));
        Assert.Contains(HttpStatusCode.Conflict, results.Select(r => r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, stockIn.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var verify = new NpgsqlCommand(
            "SELECT b.physical_qty, b.available_qty, " +
            "(SELECT COUNT(*) FROM store.inventory_movements WHERE source_type = 'INVENTORY_ADJUSTMENT' AND source_id = @request) " +
            "FROM store.inventory_balances b WHERE b.product_sku_id = @sku", db);
        verify.Parameters.AddWithValue("request", requestId);
        verify.Parameters.AddWithValue("sku", skuId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(19, reader.GetInt32(0));
        Assert.Equal(19, reader.GetInt32(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
}
