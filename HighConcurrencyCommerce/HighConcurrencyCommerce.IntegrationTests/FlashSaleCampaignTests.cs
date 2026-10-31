using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class FlashSaleCampaignTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure FlashSale pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        string path,
        object? body,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleClient(string roleKey)
    {
        var id = Guid.NewGuid();
        var email = $"flashsale_{roleKey.ToLowerInvariant()}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key=@role", db);
        role.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status) VALUES(@id,@role,@email,@hash,@name,'ACTIVE')", db);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("name", roleKey + " FlashSale User");
        await insert.ExecuteNonQueryAsync();

        var client = fixture.CreateClient();
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password = Password })
        };
        loginRequest.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        using var login = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, id);
    }

    private async Task<(Guid ProductId, Guid SkuId, decimal RegularPrice)> CreatePublishedProductWithStock(int availableStock, decimal regularPrice = 200000m)
    {
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = await fixture.OpenDatabaseAsync();

        await using var userCmd = new NpgsqlCommand("SELECT id FROM store.user_accounts LIMIT 1", db);
        var adminId = (Guid)(await userCmd.ExecuteScalarAsync())!;

        await using var prodCmd = new NpgsqlCommand(
            @"INSERT INTO store.products(id, title, publication_status, created_by, created_at, updated_at, published_at)
              VALUES(@id, @title, 'PUBLISHED', @by, @now, @now, @now)", db);
        prodCmd.Parameters.AddWithValue("id", productId);
        prodCmd.Parameters.AddWithValue("title", $"Sản phẩm FlashSale {Guid.NewGuid():N}");
        prodCmd.Parameters.AddWithValue("by", adminId);
        prodCmd.Parameters.AddWithValue("now", now);
        await prodCmd.ExecuteNonQueryAsync();

        await using var skuCmd = new NpgsqlCommand(
            @"INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status, created_at, updated_at)
              VALUES(@id, @prodId, @skuCode, 'Mặc định', @price, 'ACTIVE', @now, @now)", db);
        skuCmd.Parameters.AddWithValue("id", skuId);
        skuCmd.Parameters.AddWithValue("prodId", productId);
        skuCmd.Parameters.AddWithValue("skuCode", $"SKU-FS-{Guid.NewGuid():N}"[..20]);
        skuCmd.Parameters.AddWithValue("price", regularPrice);
        skuCmd.Parameters.AddWithValue("now", now);
        await skuCmd.ExecuteNonQueryAsync();

        await using var balCmd = new NpgsqlCommand(
            @"INSERT INTO store.inventory_balances(product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, updated_at)
              VALUES(@skuId, @qty, @qty, 0, 0, @now)", db);
        balCmd.Parameters.AddWithValue("skuId", skuId);
        balCmd.Parameters.AddWithValue("qty", availableStock);
        balCmd.Parameters.AddWithValue("now", now);
        await balCmd.ExecuteNonQueryAsync();

        return (productId, skuId, regularPrice);
    }

    [Fact]
    public async Task Admin_flash_sale_endpoints_enforce_RBAC()
    {
        using var guest = fixture.CreateClient();
        var (customer, _) = await CreateRoleClient("CUSTOMER");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");

        using (customer) using (staff) using (admin)
        {
            // Guest -> 401
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/admin/flash-sales")).StatusCode);
            using var guestCreate = await Send(guest, "admin/flash-sales", new { name = "FS", startsAt = DateTime.UtcNow, endsAt = DateTime.UtcNow.AddHours(2) });
            Assert.Equal(HttpStatusCode.Unauthorized, guestCreate.StatusCode);

            // Customer -> 403
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/flash-sales")).StatusCode);

            // Staff -> 403
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/admin/flash-sales")).StatusCode);

            // Admin -> 200
            var adminList = await admin.GetAsync("/api/v1/admin/flash-sales");
            Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);

            // Public endpoint -> 200 for guest and customer
            Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/catalog/flash-sales")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/v1/catalog/flash-sales")).StatusCode);
        }
    }

    [Fact]
    public async Task Campaign_date_validation_prevents_invalid_ranges()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (admin)
        {
            var now = DateTime.UtcNow;

            // ends_at <= starts_at
            using var invalidRange = await Send(admin, "admin/flash-sales", new
            {
                name = "Chiến dịch lỗi ngày",
                startsAt = now.AddHours(5),
                endsAt = now.AddHours(2)
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);
            var err = await invalidRange.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("VALIDATION_ERROR", err.GetProperty("code").GetString());

            // ends_at <= now
            using var pastEnd = await Send(admin, "admin/flash-sales", new
            {
                name = "Chiến dịch quá khứ",
                startsAt = now.AddHours(-10),
                endsAt = now.AddHours(-2)
            });
            Assert.Equal(HttpStatusCode.BadRequest, pastEnd.StatusCode);

            // Valid range -> 201 Created
            using var valid = await Send(admin, "admin/flash-sales", new
            {
                name = "Chiến dịch hợp lệ 11.11",
                startsAt = now.AddHours(1),
                endsAt = now.AddHours(3)
            });
            Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
            var body = await valid.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("DRAFT", body.GetProperty("campaignStatus").GetString());
            Assert.Equal("Chiến dịch hợp lệ 11.11", body.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Offer_invariants_enforce_price_quota_and_uniqueness()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (admin)
        {
            var now = DateTime.UtcNow;
            var (_, skuId, regularPrice) = await CreatePublishedProductWithStock(availableStock: 50, regularPrice: 500000m);

            // Tạo campaign DRAFT
            using var campRes = await Send(admin, "admin/flash-sales", new
            {
                name = "FS Offer Test",
                startsAt = now.AddHours(1),
                endsAt = now.AddHours(4)
            });
            Assert.Equal(HttpStatusCode.Created, campRes.StatusCode);
            var campaignId = (await campRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

            // 1. Sale price >= regular price -> 400
            using var higherPrice = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 550000m,
                quotaTotal = 10,
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.BadRequest, higherPrice.StatusCode);

            // 2. Sale price <= 0 -> 400
            using var zeroPrice = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 0m,
                quotaTotal = 10,
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.BadRequest, zeroPrice.StatusCode);

            // 3. Quota > available inventory -> 400 INSUFFICIENT_STOCK
            using var excessQuota = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 300000m,
                quotaTotal = 100, // available is 50
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.BadRequest, excessQuota.StatusCode);
            Assert.Equal("INSUFFICIENT_STOCK", (await excessQuota.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            // 4. Thêm offer hợp lệ -> 201 Created
            using var validOffer = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 350000m,
                quotaTotal = 20,
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.Created, validOffer.StatusCode);
            var offerId = (await validOffer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

            // 5. Thêm trùng SKU vào cùng campaign -> 409 CONFLICT
            using var duplicateOffer = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 300000m,
                quotaTotal = 10,
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.Conflict, duplicateOffer.StatusCode);

            // 6. Xóa offer -> 204 NoContent
            using var delRes = await Send(admin, $"admin/flash-sales/{campaignId}/offers/{offerId}", null, HttpMethod.Delete);
            Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);
        }
    }

    [Fact]
    public async Task Publish_and_cancel_lifecycle_respects_state_machine_and_audit()
    {
        var (admin, adminId) = await CreateRoleClient("ADMIN");
        using (admin)
        {
            var now = DateTime.UtcNow;
            var (_, skuId, _) = await CreatePublishedProductWithStock(availableStock: 30, regularPrice: 200000m);

            // Tạo campaign DRAFT bắt đầu ngay (active ngay khi publish)
            using var campRes = await Send(admin, "admin/flash-sales", new
            {
                name = "Chiến dịch Sale Ngay",
                startsAt = now.AddMinutes(-5),
                endsAt = now.AddHours(2)
            });
            Assert.Equal(HttpStatusCode.Created, campRes.StatusCode);
            var campaignId = (await campRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

            // Publish khi chưa có offer -> 400 CANNOT_PUBLISH
            using var emptyPub = await Send(admin, $"admin/flash-sales/{campaignId}/publish", null);
            Assert.Equal(HttpStatusCode.BadRequest, emptyPub.StatusCode);
            Assert.Equal("CANNOT_PUBLISH", (await emptyPub.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            // Thêm offer
            using var offerRes = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 120000m,
                quotaTotal = 15,
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.Created, offerRes.StatusCode);

            // Publish thành công -> ACTIVE (vì starts_at trong quá khứ)
            using var pubRes = await Send(admin, $"admin/flash-sales/{campaignId}/publish", null);
            Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);
            var pubBody = await pubRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ACTIVE", pubBody.GetProperty("campaignStatus").GetString());

            // Publish lại khi đã ACTIVE -> 409 INVALID_STATE_TRANSITION
            using var rePub = await Send(admin, $"admin/flash-sales/{campaignId}/publish", null);
            Assert.Equal(HttpStatusCode.Conflict, rePub.StatusCode);

            // Thêm offer khi đã ACTIVE -> 409 INVALID_STATE_TRANSITION
            using var addActiveOffer = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 110000m,
                quotaTotal = 5,
                perCustomerLimit = 1
            });
            Assert.Equal(HttpStatusCode.Conflict, addActiveOffer.StatusCode);

            // Xem hiệu suất campaign
            var perfRes = await admin.GetAsync($"/api/v1/admin/flash-sales/{campaignId}/performance");
            Assert.Equal(HttpStatusCode.OK, perfRes.StatusCode);
            var perf = await perfRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(15, perf.GetProperty("totalQuota").GetInt32());

            // Public catalog thấy campaign này
            using var guest = fixture.CreateClient();
            var publicRes = await guest.GetAsync("/api/v1/catalog/flash-sales");
            Assert.Equal(HttpStatusCode.OK, publicRes.StatusCode);
            var publicList = await publicRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(publicList.EnumerateArray(), c => c.GetProperty("id").GetString() == campaignId);

            // Cancel campaign
            using var cancelRes = await Send(admin, $"admin/flash-sales/{campaignId}/cancel", new
            {
                reason = "Dừng chương trình để bảo dưỡng"
            });
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);
            var cancelBody = await cancelRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CANCELLED", cancelBody.GetProperty("campaignStatus").GetString());

            // Cancel lần 2 -> 409 INVALID_STATE_TRANSITION
            using var reCancel = await Send(admin, $"admin/flash-sales/{campaignId}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, reCancel.StatusCode);

            // Kiểm tra Audit Logs
            await using var db = await fixture.OpenDatabaseAsync();
            await using var auditCmd = new NpgsqlCommand(
                "SELECT action FROM store.audit_logs WHERE entity_id = @id ORDER BY occurred_at", db);
            auditCmd.Parameters.AddWithValue("id", Guid.Parse(campaignId));
            await using var reader = await auditCmd.ExecuteReaderAsync();
            var actions = new List<string>();
            while (await reader.ReadAsync())
            {
                actions.Add(reader.GetString(0));
            }

            Assert.Contains("FLASH_SALE_CAMPAIGN_CREATED", actions);
            Assert.Contains("FLASH_SALE_CAMPAIGN_PUBLISHED", actions);
            Assert.Contains("FLASH_SALE_CAMPAIGN_CANCELLED", actions);
        }
    }

    [Fact]
    public async Task Concurrent_cancel_requests_produce_exactly_one_cancellation_and_one_audit_log()
    {
        var (admin1, _) = await CreateRoleClient("ADMIN");
        var (admin2, _) = await CreateRoleClient("ADMIN");
        using (admin1) using (admin2)
        {
            var now = DateTime.UtcNow;
            var (_, skuId, _) = await CreatePublishedProductWithStock(availableStock: 20, regularPrice: 300000m);

            // Tạo campaign và publish
            using var campRes = await Send(admin1, "admin/flash-sales", new
            {
                name = "Race Cancel Test",
                startsAt = now.AddMinutes(-1),
                endsAt = now.AddHours(2)
            });
            var campaignId = (await campRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

            using var offerRes = await Send(admin1, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId,
                salePrice = 180000m,
                quotaTotal = 10,
                perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.Created, offerRes.StatusCode);

            using var pubRes = await Send(admin1, $"admin/flash-sales/{campaignId}/publish", null);
            Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);

            // 2 Admin cùng bấm Cancel đồng thời
            var task1 = Send(admin1, $"admin/flash-sales/{campaignId}/cancel", new { reason = "Admin 1 cancel" });
            var task2 = Send(admin2, $"admin/flash-sales/{campaignId}/cancel", new { reason = "Admin 2 cancel" });

            var responses = await Task.WhenAll(task1, task2);
            var statusCodes = responses.Select(r => r.StatusCode).ToList();

            // Đúng 1 request thành công 200, 1 request nhận 409
            Assert.Contains(HttpStatusCode.OK, statusCodes);
            Assert.Contains(HttpStatusCode.Conflict, statusCodes);

            // Kiểm tra DB: chỉ có 1 Audit Log FLASH_SALE_CAMPAIGN_CANCELLED
            await using var db = await fixture.OpenDatabaseAsync();
            await using var countCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM store.audit_logs WHERE entity_id = @id AND action = 'FLASH_SALE_CAMPAIGN_CANCELLED'", db);
            countCmd.Parameters.AddWithValue("id", Guid.Parse(campaignId));
            var cancelAuditCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            Assert.Equal(1, cancelAuditCount);
        }
    }

    [Fact]
    public async Task Scheduled_campaign_is_activated_and_ended_by_lifecycle_worker()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (admin)
        {
            var now = DateTime.UtcNow;
            var (_, skuId, _) = await CreatePublishedProductWithStock(20, 300000m);
            using var campaignResponse = await Send(admin, "admin/flash-sales", new
            {
                name = "Lifecycle worker campaign",
                startsAt = now.AddSeconds(3),
                endsAt = now.AddSeconds(6)
            });
            var campaignId = (await campaignResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            using var offer = await Send(admin, $"admin/flash-sales/{campaignId}/offers", new
            {
                productSkuId = skuId, salePrice = 200000m, quotaTotal = 5, perCustomerLimit = 2
            });
            Assert.Equal(HttpStatusCode.Created, offer.StatusCode);
            using var publish = await Send(admin, $"admin/flash-sales/{campaignId}/publish", null);
            Assert.Equal("SCHEDULED", (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("campaignStatus").GetString());

            async Task<string> Status()
            {
                using var response = await admin.GetAsync($"/api/v1/admin/flash-sales/{campaignId}");
                return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("campaignStatus").GetString()!;
            }
            async Task WaitFor(string expected, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (await Status() == expected) return;
                    await Task.Delay(100);
                }
                Assert.Equal(expected, await Status());
            }

            await WaitFor("ACTIVE", TimeSpan.FromSeconds(5));
            await WaitFor("ENDED", TimeSpan.FromSeconds(5));

            await using var db = await fixture.OpenDatabaseAsync();
            await using var audit = new NpgsqlCommand(
                "SELECT action FROM store.audit_logs WHERE entity_id=@id AND actor_type='WORKER' ORDER BY occurred_at", db);
            audit.Parameters.AddWithValue("id", campaignId);
            await using var reader = await audit.ExecuteReaderAsync();
            var actions = new List<string>();
            while (await reader.ReadAsync()) actions.Add(reader.GetString(0));
            Assert.Contains("FLASH_SALE_CAMPAIGN_ACTIVE", actions);
            Assert.Contains("FLASH_SALE_CAMPAIGN_ENDED", actions);
        }
    }
}

