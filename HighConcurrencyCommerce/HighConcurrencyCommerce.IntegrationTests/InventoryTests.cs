using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class InventoryTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure Staff/Admin pass 123!";
    private static string Email(string prefix) => $"{prefix}_{Guid.NewGuid():N}@example.invalid";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task<(HttpClient Client, Guid UserId)> CreateStaffClient(CatalogFixture fixture, string prefix)
    {
        return await CreateUserWithRole(fixture, "STAFF", prefix, "Staff Kho Vận");
    }

    private static async Task<(HttpClient Client, Guid UserId)> CreateAdminClient(CatalogFixture fixture, string prefix)
    {
        return await CreateUserWithRole(fixture, "ADMIN", prefix, "Admin Quản Trị");
    }

    private static async Task<(HttpClient Client, Guid UserId)> CreateUserWithRole(CatalogFixture fixture, string roleKey, string prefix, string fullName)
    {
        var email = Email(prefix);
        var userId = Guid.NewGuid();

        await using var db = await fixture.OpenDatabaseAsync();
        await using (var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @roleKey", db))
        {
            roleCmd.Parameters.AddWithValue("roleKey", roleKey);
            var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
            var hash = hasher.HashPassword(new object(), Password);

            await using var insertCmd = new NpgsqlCommand(
                "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status) " +
                "VALUES (@userId, @roleId, @email, @hash, @fullName, 'ACTIVE')", db);
            insertCmd.Parameters.AddWithValue("userId", userId);
            insertCmd.Parameters.AddWithValue("roleId", roleId);
            insertCmd.Parameters.AddWithValue("email", email);
            insertCmd.Parameters.AddWithValue("hash", hash);
            insertCmd.Parameters.AddWithValue("fullName", fullName);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var client = fixture.CreateClient();
        using var loginRes = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
        return (client, userId);
    }

    private static async Task<HttpClient> CreateCustomerClient(CatalogFixture fixture)
    {
        var client = fixture.CreateClient();
        var email = Email("cust");
        using var reg = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Customer" });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task RBAC_enforces_proper_access_controls_on_inventory()
    {
        using var guest = fixture.CreateClient();
        var (staff, _) = await CreateStaffClient(fixture, "staff_inv_rbac");
        var (admin, _) = await CreateAdminClient(fixture, "admin_inv_rbac");
        using var customer = await CreateCustomerClient(fixture);

        var skuId = Guid.Parse("30000000-0000-0000-0000-000000000001");

        // 1. Guest -> 401 Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/staff/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/api/v1/staff/inventory/{skuId}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/api/v1/staff/inventory/stock-in", new { productSkuId = skuId, quantity = 5 })).StatusCode);

        // 2. Customer -> 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/staff/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/v1/staff/inventory/{skuId}/movements")).StatusCode);
        var custStockInRes = await Send(customer, "staff/inventory/stock-in", new { productSkuId = skuId, quantity = 5 });
        Assert.Equal(HttpStatusCode.Forbidden, custStockInRes.StatusCode);

        // 3. Admin -> 403 Forbidden (Chỉ Staff được vận hành kho)
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/staff/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/staff/inventory/{skuId}/movements")).StatusCode);
        var adminStockInRes = await Send(admin, "staff/inventory/stock-in", new { productSkuId = skuId, quantity = 5 });
        Assert.Equal(HttpStatusCode.Forbidden, adminStockInRes.StatusCode);

        // 4. Staff -> 200 OK
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/staff/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/staff/inventory/{skuId}/movements")).StatusCode);
    }

    [Fact]
    public async Task Complete_stock_in_flow_preserves_partition_invariant_and_records_ledger()
    {
        var (staff, staffId) = await CreateStaffClient(fixture, "staff_flow_inv");

        // Seed SKU 1 has initial: physical=10, available=0, reserved=5, allocated=5
        var skuId = Guid.Parse("30000000-0000-0000-0000-000000000001");

        // 1. Check current inventory summary
        using var listRes = await staff.GetAsync("/api/v1/staff/inventory");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var summaries = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(summaries.GetArrayLength() > 0);

        var targetSku = summaries.EnumerateArray().First(s => s.GetProperty("skuId").GetGuid() == skuId);
        var initialPhysical = targetSku.GetProperty("physicalQty").GetInt32();
        var initialAvailable = targetSku.GetProperty("availableQty").GetInt32();
        var initialReserved = targetSku.GetProperty("reservedQty").GetInt32();
        var initialAllocated = targetSku.GetProperty("allocatedQty").GetInt32();

        Assert.Equal(initialPhysical, initialAvailable + initialReserved + initialAllocated);

        // 2. Stock in 25 units
        var stockInPayload = new
        {
            productSkuId = skuId,
            quantity = 25,
            sourceType = "PURCHASE_RECEIPT",
            note = "Nhập hàng từ kho tổng"
        };

        using var stockInRes = await Send(staff, "staff/inventory/stock-in", stockInPayload);
        Assert.Equal(HttpStatusCode.OK, stockInRes.StatusCode);

        var result = await stockInRes.Content.ReadFromJsonAsync<JsonElement>();
        var newPhysical = result.GetProperty("physicalQty").GetInt32();
        var newAvailable = result.GetProperty("availableQty").GetInt32();
        var newReserved = result.GetProperty("reservedQty").GetInt32();
        var newAllocated = result.GetProperty("allocatedQty").GetInt32();

        Assert.Equal(initialPhysical + 25, newPhysical);
        Assert.Equal(initialAvailable + 25, newAvailable);
        Assert.Equal(initialReserved, newReserved); // Reserved unchanged
        Assert.Equal(initialAllocated, newAllocated); // Allocated unchanged
        Assert.Equal(newPhysical, newAvailable + newReserved + newAllocated); // Partition invariant preserved!

        // 3. Verify Movement ledger
        using var movRes = await staff.GetAsync($"/api/v1/staff/inventory/{skuId}/movements");
        Assert.Equal(HttpStatusCode.OK, movRes.StatusCode);
        var movements = await movRes.Content.ReadFromJsonAsync<JsonElement>();
        var latestMov = movements.EnumerateArray().First();

        Assert.Equal("STOCK_IN", latestMov.GetProperty("movementType").GetString());
        Assert.Equal(25, latestMov.GetProperty("qtyDelta").GetInt32());
        Assert.Equal("PURCHASE_RECEIPT", latestMov.GetProperty("sourceType").GetString());
        Assert.Equal(staffId, latestMov.GetProperty("actorId").GetGuid());
        Assert.Equal("Staff Kho Vận", latestMov.GetProperty("actorName").GetString());
        Assert.Equal("Nhập hàng từ kho tổng", latestMov.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Stock_in_validation_fails_on_zero_quantity_or_missing_sku()
    {
        var (staff, _) = await CreateStaffClient(fixture, "staff_val_inv");
        var skuId = Guid.Parse("30000000-0000-0000-0000-000000000001");

        // Zero or negative quantity -> 400 Bad Request
        using var resZero = await Send(staff, "staff/inventory/stock-in", new { productSkuId = skuId, quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, resZero.StatusCode);

        using var resNeg = await Send(staff, "staff/inventory/stock-in", new { productSkuId = skuId, quantity = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, resNeg.StatusCode);

        // Missing SKU -> 404 Not Found
        var missingSkuId = Guid.NewGuid();
        using var resMissing = await Send(staff, "staff/inventory/stock-in", new { productSkuId = missingSkuId, quantity = 10 });
        Assert.Equal(HttpStatusCode.NotFound, resMissing.StatusCode);
        var err = await resMissing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stock_in_with_same_source_is_idempotent()
    {
        var (staff, _) = await CreateStaffClient(fixture, "staff_idemp_inv");
        var skuId = Guid.Parse("30000000-0000-0000-0000-000000000002");

        var sourceId = Guid.NewGuid();
        var payload = new
        {
            productSkuId = skuId,
            quantity = 15,
            sourceType = "PURCHASE_RECEIPT",
            sourceId,
            note = "Idempotent stock-in batch"
        };

        // First attempt
        using var res1 = await Send(staff, "staff/inventory/stock-in", payload);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var body1 = await res1.Content.ReadFromJsonAsync<JsonElement>();
        var physicalAfterFirst = body1.GetProperty("physicalQty").GetInt32();
        var movementId1 = body1.GetProperty("movementId").GetGuid();

        // Second attempt with exact same sourceId & sourceType (Replay)
        using var res2 = await Send(staff, "staff/inventory/stock-in", payload);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var body2 = await res2.Content.ReadFromJsonAsync<JsonElement>();
        var physicalAfterSecond = body2.GetProperty("physicalQty").GetInt32();
        var movementId2 = body2.GetProperty("movementId").GetGuid();

        // Stock quantity must NOT be added twice!
        Assert.Equal(physicalAfterFirst, physicalAfterSecond);
        Assert.Equal(movementId1, movementId2);

        // Third attempt with same sourceId & sourceType but DIFFERENT quantity -> 409 IDEMPOTENCY_CONFLICT
        var conflictingQtyPayload = new
        {
            productSkuId = skuId,
            quantity = 50, // Changed from 15 to 50
            sourceType = "PURCHASE_RECEIPT",
            sourceId,
            note = "Idempotent stock-in batch"
        };
        using var resConflictQty = await Send(staff, "staff/inventory/stock-in", conflictingQtyPayload);
        Assert.Equal(HttpStatusCode.Conflict, resConflictQty.StatusCode);
        var errBodyQty = await resConflictQty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_CONFLICT", errBodyQty.GetProperty("code").GetString());

        // Fourth attempt with same sourceId & sourceType but DIFFERENT note -> 409 IDEMPOTENCY_CONFLICT
        var conflictingNotePayload = new
        {
            productSkuId = skuId,
            quantity = 15,
            sourceType = "PURCHASE_RECEIPT",
            sourceId,
            note = "Nội dung ghi chú đã bị sửa đổi" // Changed note
        };
        using var resConflictNote = await Send(staff, "staff/inventory/stock-in", conflictingNotePayload);
        Assert.Equal(HttpStatusCode.Conflict, resConflictNote.StatusCode);
        var errBodyNote = await resConflictNote.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_CONFLICT", errBodyNote.GetProperty("code").GetString());

        // Verify balance in database remains unchanged
        await using var db = await fixture.OpenDatabaseAsync();
        await using var verifyCmd = new NpgsqlCommand("SELECT physical_qty FROM store.inventory_balances WHERE product_sku_id = @skuId", db);
        verifyCmd.Parameters.AddWithValue("skuId", skuId);
        var finalPhysical = (int)(await verifyCmd.ExecuteScalarAsync())!;
        Assert.Equal(physicalAfterFirst, finalPhysical);
    }

    [Fact]
    public async Task Concurrent_stock_in_serializes_updates_and_prevents_lost_updates()
    {
        var (staff, _) = await CreateStaffClient(fixture, "staff_race_inv");
        var skuId = Guid.Parse("30000000-0000-0000-0000-000000000004");

        // Read initial physical quantity
        await using var db = await fixture.OpenDatabaseAsync();
        await using var readCmd = new NpgsqlCommand("SELECT physical_qty, available_qty FROM store.inventory_balances WHERE product_sku_id = @skuId", db);
        readCmd.Parameters.AddWithValue("skuId", skuId);
        int initialPhysical = 0;
        int initialAvailable = 0;
        await using (var reader = await readCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                initialPhysical = reader.GetInt32(0);
                initialAvailable = reader.GetInt32(1);
            }
        }

        // Send 8 stock-in requests concurrently, each adding 10 units with distinct sourceId
        const int concurrentRequests = 8;
        const int qtyPerRequest = 10;

        var tasks = Enumerable.Range(0, concurrentRequests).Select(i =>
        {
            var client = fixture.CreateClient();
            var payload = new
            {
                productSkuId = skuId,
                quantity = qtyPerRequest,
                sourceType = "CONCURRENT_TEST",
                sourceId = Guid.NewGuid(),
                note = $"Concurrent thread {i}"
            };
            return Send(staff, "staff/inventory/stock-in", payload);
        }).ToArray();

        var responses = await Task.WhenAll(tasks);
        foreach (var res in responses)
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            res.Dispose();
        }

        // Verify final database state: physical and available must have increased by exactly 80!
        await using var verifyCmd = new NpgsqlCommand(
            "SELECT physical_qty, available_qty, reserved_qty, allocated_qty " +
            "FROM store.inventory_balances WHERE product_sku_id = @skuId", db);
        verifyCmd.Parameters.AddWithValue("skuId", skuId);

        await using (var verifyReader = await verifyCmd.ExecuteReaderAsync())
        {
            Assert.True(await verifyReader.ReadAsync());
            var finalPhysical = verifyReader.GetInt32(0);
            var finalAvailable = verifyReader.GetInt32(1);
            var finalReserved = verifyReader.GetInt32(2);
            var finalAllocated = verifyReader.GetInt32(3);

            Assert.Equal(initialPhysical + (concurrentRequests * qtyPerRequest), finalPhysical);
            Assert.Equal(initialAvailable + (concurrentRequests * qtyPerRequest), finalAvailable);
            Assert.Equal(finalPhysical, finalAvailable + finalReserved + finalAllocated);
        }
    }

    [Fact]
    public async Task Concurrent_stock_in_with_same_source_does_not_double_count_or_crash()
    {
        var (staff, _) = await CreateStaffClient(fixture, "staff_race_dup_source");
        var skuId = Guid.Parse("30000000-0000-0000-0000-000000000005");

        var sharedSourceId = Guid.NewGuid();
        var payload = new
        {
            productSkuId = skuId,
            quantity = 20,
            sourceType = "SHARED_BATCH",
            sourceId = sharedSourceId,
            note = "Concurrent duplicate source test"
        };

        var task1 = Send(staff, "staff/inventory/stock-in", payload);
        var task2 = Send(staff, "staff/inventory/stock-in", payload);

        var responses = await Task.WhenAll(task1, task2);
        using var res1 = responses[0];
        using var res2 = responses[1];

        var statusCodes = new[] { res1.StatusCode, res2.StatusCode };
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statusCodes);

        // At least one must succeed with 200 OK; the second is either 200 (idempotent replay) or 409 (conflict)
        Assert.Contains(HttpStatusCode.OK, statusCodes);

        // Check movements ledger in DB: MUST have exactly 1 movement record for this sourceId
        await using var db = await fixture.OpenDatabaseAsync();
        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM store.inventory_movements WHERE source_id = @sourceId", db);
        countCmd.Parameters.AddWithValue("sourceId", sharedSourceId);
        var count = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count);
    }
}

