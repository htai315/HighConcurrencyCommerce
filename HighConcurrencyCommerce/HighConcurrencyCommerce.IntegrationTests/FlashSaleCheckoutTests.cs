using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class FlashSaleCheckoutTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure FlashSaleCheckout pass 123!";

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
        HttpMethod? method = null,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleClient(string roleKey)
    {
        var id = Guid.NewGuid();
        var email = $"fs_co_{roleKey.ToLowerInvariant()}_{Guid.NewGuid():N}@example.invalid";
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
        insert.Parameters.AddWithValue("name", roleKey + " User");
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

    private async Task<Guid> CreateAddress(HttpClient client)
    {
        using var response = await Send(client, "account/addresses", new
        {
            recipientName = "Người Nhận FlashSale",
            phone = "0987654321",
            addressLine = "123 Đường Mua Sắm",
            ward = "Phường Bến Nghé",
            province = "TP. Hồ Chí Minh",
            isDefault = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private async Task<(Guid CampaignId, Guid OfferId, Guid SkuId, decimal RegularPrice, decimal SalePrice)> SetupActiveFlashSale(
        HttpClient adminClient,
        int stock = 50,
        int quota = 10,
        short perCustomerLimit = 2,
        decimal regularPrice = 200000m,
        decimal salePrice = 120000m)
    {
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = await fixture.OpenDatabaseAsync();
        await using var userCmd = new NpgsqlCommand("SELECT id FROM store.user_accounts LIMIT 1", db);
        var adminDbId = (Guid)(await userCmd.ExecuteScalarAsync())!;

        await using var prodCmd = new NpgsqlCommand(
            @"INSERT INTO store.products(id, title, publication_status, created_by, created_at, updated_at, published_at)
              VALUES(@id, @title, 'PUBLISHED', @by, @now, @now, @now)", db);
        prodCmd.Parameters.AddWithValue("id", productId);
        prodCmd.Parameters.AddWithValue("title", $"Sản phẩm FlashSale CO {Guid.NewGuid():N}");
        prodCmd.Parameters.AddWithValue("by", adminDbId);
        prodCmd.Parameters.AddWithValue("now", now);
        await prodCmd.ExecuteNonQueryAsync();

        await using var skuCmd = new NpgsqlCommand(
            @"INSERT INTO store.product_skus(id, product_id, sku_code, option_label, regular_price, sku_status, created_at, updated_at)
              VALUES(@id, @prodId, @skuCode, 'Mặc định', @price, 'ACTIVE', @now, @now)", db);
        skuCmd.Parameters.AddWithValue("id", skuId);
        skuCmd.Parameters.AddWithValue("prodId", productId);
        skuCmd.Parameters.AddWithValue("skuCode", $"SKU-FSCO-{Guid.NewGuid():N}"[..20]);
        skuCmd.Parameters.AddWithValue("price", regularPrice);
        skuCmd.Parameters.AddWithValue("now", now);
        await skuCmd.ExecuteNonQueryAsync();

        await using var balCmd = new NpgsqlCommand(
            @"INSERT INTO store.inventory_balances(product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, updated_at)
              VALUES(@skuId, @qty, @qty, 0, 0, @now)", db);
        balCmd.Parameters.AddWithValue("skuId", skuId);
        balCmd.Parameters.AddWithValue("qty", stock);
        balCmd.Parameters.AddWithValue("now", now);
        await balCmd.ExecuteNonQueryAsync();

        // Admin tạo campaign
        using var campRes = await Send(adminClient, "admin/flash-sales", new
        {
            name = $"Campaign FS {Guid.NewGuid():N}",
            startsAt = now.AddMinutes(-5),
            endsAt = now.AddHours(2)
        });
        Assert.Equal(HttpStatusCode.Created, campRes.StatusCode);
        var campaignId = Guid.Parse((await campRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

        // Admin thêm offer
        using var offerRes = await Send(adminClient, $"admin/flash-sales/{campaignId}/offers", new
        {
            productSkuId = skuId,
            salePrice,
            quotaTotal = quota,
            perCustomerLimit
        });
        Assert.Equal(HttpStatusCode.Created, offerRes.StatusCode);
        var offerId = Guid.Parse((await offerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

        // Admin publish campaign -> ACTIVE
        using var pubRes = await Send(adminClient, $"admin/flash-sales/{campaignId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);

        return (campaignId, offerId, skuId, regularPrice, salePrice);
    }

    [Fact]
    public async Task Flash_sale_checkout_succeeds_with_sale_price_and_allocates_quota()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (customer, customerId) = await CreateRoleClient("CUSTOMER");
        using (admin) using (customer)
        {
            var addressId = await CreateAddress(customer);
            var (_, offerId, skuId, _, salePrice) = await SetupActiveFlashSale(admin, stock: 20, quota: 10, perCustomerLimit: 2, regularPrice: 200000m, salePrice: 130000m);

            var key = Guid.NewGuid().ToString("N");
            using var checkoutRes = await Send(customer, "checkouts", new
            {
                shippingAddressId = addressId,
                paymentMethod = "COD",
                lines = new[]
                {
                    new { productSkuId = skuId, quantity = 2, flashSaleOfferId = (Guid?)offerId }
                }
            }, idempotencyKey: key);

            Assert.Equal(HttpStatusCode.Created, checkoutRes.StatusCode);
            var result = await checkoutRes.Content.ReadFromJsonAsync<JsonElement>();

            // UnitPrice phải là giá sale 130000 đ
            var item = result.GetProperty("items")[0];
            Assert.Equal(salePrice, item.GetProperty("unitPrice").GetDecimal());
            Assert.Equal(salePrice * 2, item.GetProperty("lineTotal").GetDecimal());
            Assert.Equal(salePrice * 2, result.GetProperty("totalAmount").GetDecimal());
            var orderId = Guid.Parse(result.GetProperty("orderId").GetString()!);

            // Kiểm tra DB: store.flash_sale_allocations có 1 dòng HELD, quantity = 2
            await using var db = await fixture.OpenDatabaseAsync();
            await using var allocCmd = new NpgsqlCommand(
                "SELECT allocation_status, quantity FROM store.flash_sale_allocations WHERE offer_id = @offId", db);
            allocCmd.Parameters.AddWithValue("offId", offerId);
            await using var reader = await allocCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("HELD", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
            await reader.CloseAsync();

            // Kiểm tra DB: store.flash_sale_offers có quota_held = 2, quota_sold = 0
            await using var offerCmd = new NpgsqlCommand(
                "SELECT quota_held, quota_sold FROM store.flash_sale_offers WHERE id = @id", db);
            offerCmd.Parameters.AddWithValue("id", offerId);
            await using var offerReader = await offerCmd.ExecuteReaderAsync();
            Assert.True(await offerReader.ReadAsync());
            Assert.Equal(2, offerReader.GetInt32(0));
            Assert.Equal(0, offerReader.GetInt32(1));
            await offerReader.CloseAsync();

            // Replay cùng Idempotency-Key trả 200 OK
            using var replayRes = await Send(customer, "checkouts", new
            {
                shippingAddressId = addressId,
                paymentMethod = "COD",
                lines = new[]
                {
                    new { productSkuId = skuId, quantity = 2, flashSaleOfferId = (Guid?)offerId }
                }
            }, idempotencyKey: key);
            Assert.Equal(HttpStatusCode.OK, replayRes.StatusCode);
            Assert.True(replayRes.Headers.Contains("Idempotency-Replayed"));
        }
    }

    [Fact]
    public async Task Flash_sale_checkout_rejects_when_quota_exceeded()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (cust1, _) = await CreateRoleClient("CUSTOMER");
        var (cust2, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (cust1) using (cust2)
        {
            var addr1 = await CreateAddress(cust1);
            var addr2 = await CreateAddress(cust2);

            // Quota = 3
            var (_, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 20, quota: 3, perCustomerLimit: 5);

            // Cust1 mua 2 suất -> thành công (còn 1 suất)
            using var res1 = await Send(cust1, "checkouts", new
            {
                shippingAddressId = addr1,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 2, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

            // Cust2 muốn mua 2 suất -> chỉ còn 1 suất -> nhận 409 FLASH_SALE_QUOTA_EXCEEDED
            using var res2 = await Send(cust2, "checkouts", new
            {
                shippingAddressId = addr2,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 2, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));

            Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
            var err = await res2.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("FLASH_SALE_QUOTA_EXCEEDED", err.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Flash_sale_checkout_rejects_when_per_customer_limit_exceeded()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (customer, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (customer)
        {
            var addressId = await CreateAddress(customer);

            // Quota = 10 nhưng perCustomerLimit = 2
            var (_, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 30, quota: 10, perCustomerLimit: 2);

            // Đơn 1: Mua 2 suất -> thành công
            using var res1 = await Send(customer, "checkouts", new
            {
                shippingAddressId = addressId,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 2, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

            // Đơn 2: Cố mua thêm 1 suất nữa -> nhận 409 FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED
            using var res2 = await Send(customer, "checkouts", new
            {
                shippingAddressId = addressId,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));

            Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
            var err = await res2.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED", err.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Flash_sale_checkout_rejects_when_campaign_not_active()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (customer, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (customer)
        {
            var addressId = await CreateAddress(customer);
            var (campaignId, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 20, quota: 10);

            // Admin hủy chiến dịch
            using var cancelRes = await Send(admin, $"admin/flash-sales/{campaignId}/cancel", new { reason = "Dừng đột xuất" });
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

            // Customer checkout ưu đãi này -> 409 FLASH_SALE_CAMPAIGN_NOT_ACTIVE
            using var checkoutRes = await Send(customer, "checkouts", new
            {
                shippingAddressId = addressId,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));

            Assert.Equal(HttpStatusCode.Conflict, checkoutRes.StatusCode);
            var err = await checkoutRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("FLASH_SALE_CAMPAIGN_NOT_ACTIVE", err.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Full_lifecycle_allocation_transitions_across_confirmation_cancel_and_expiry()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (cust1, _) = await CreateRoleClient("CUSTOMER");
        var (cust2, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (staff) using (cust1) using (cust2)
        {
            var addr1 = await CreateAddress(cust1);
            var addr2 = await CreateAddress(cust2);

            // Quota = 10, Limit = 5
            var (campaignId, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 50, quota: 10, perCustomerLimit: 5);

            // 1. Cust1 checkout 2 suất -> HELD (QuotaHeld = 2, QuotaSold = 0)
            using var coRes1 = await Send(cust1, "checkouts", new
            {
                shippingAddressId = addr1,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 2, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, coRes1.StatusCode);
            var orderId1 = Guid.Parse((await coRes1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetString()!);

            // Staff xác nhận COD đơn 1 -> HELD chuyển thành SOLD (QuotaHeld = 0, QuotaSold = 2)
            using var confirmRes = await Send(staff, $"staff/orders/{orderId1}/confirm-cod", null);
            Assert.Equal(HttpStatusCode.OK, confirmRes.StatusCode);

            await using var db = await fixture.OpenDatabaseAsync();
            await using (var checkCmd = new NpgsqlCommand("SELECT quota_held, quota_sold FROM store.flash_sale_offers WHERE id = @id", db))
            {
                checkCmd.Parameters.AddWithValue("id", offerId);
                await using var r = await checkCmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                Assert.Equal(0, r.GetInt32(0));
                Assert.Equal(2, r.GetInt32(1));
            }

            // 2. Cust2 checkout 3 suất -> HELD (QuotaHeld = 3, QuotaSold = 2)
            using var coRes2 = await Send(cust2, "checkouts", new
            {
                shippingAddressId = addr2,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 3, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, coRes2.StatusCode);
            var orderId2 = Guid.Parse((await coRes2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetString()!);

            // Cust2 tự hủy đơn -> HELD chuyển thành RELEASED (QuotaHeld giảm từ 3 về 0)
            using var cancelRes = await Send(cust2, $"orders/{orderId2}/cancel", new { reason = "Đổi ý không mua nữa" });
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

            await using (var checkCmd2 = new NpgsqlCommand("SELECT quota_held, quota_sold FROM store.flash_sale_offers WHERE id = @id", db))
            {
                checkCmd2.Parameters.AddWithValue("id", offerId);
                await using var r = await checkCmd2.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                Assert.Equal(0, r.GetInt32(0));
                Assert.Equal(2, r.GetInt32(1));
            }

            // Quota khả dụng lúc này: 10 - (0 + 2) = 8 suất!
            var perfRes = await admin.GetAsync($"/api/v1/admin/flash-sales/{campaignId}/performance");
            Assert.Equal(HttpStatusCode.OK, perfRes.StatusCode);
            var perf = await perfRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(8, perf.GetProperty("remainingQuota").GetInt32());
            // Kiểm tra DB trực tiếp
            await using var allocCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM store.flash_sale_allocations WHERE offer_id = @id AND allocation_status = 'RELEASED'", db);
            allocCmd.Parameters.AddWithValue("id", offerId);
            var releasedCount = Convert.ToInt32(await allocCmd.ExecuteScalarAsync());
            Assert.Equal(1, releasedCount);
        }
    }

    [Fact]
    public async Task High_concurrency_race_for_limited_flash_sale_quota_allocates_exact_quota_without_oversell()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (admin)
        {
            // Thiết lập Offer có quota = 5 (tồn kho 50, perCustomerLimit = 1)
            var (_, offerId, skuId, _, salePrice) = await SetupActiveFlashSale(admin, stock: 50, quota: 5, perCustomerLimit: 1);

            // Tạo 15 khách hàng riêng biệt
            const int totalCustomers = 15;
            var customerClients = new List<(HttpClient Client, Guid AddressId)>();
            for (var i = 0; i < totalCustomers; i++)
            {
                var (client, _) = await CreateRoleClient("CUSTOMER");
                var addrId = await CreateAddress(client);
                customerClients.Add((client, addrId));
            }

            // Đồng thời xuất phát bằng StartGate
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = customerClients.Select(async c =>
            {
                await startGate.Task;
                return await Send(c.Client, "checkouts", new
                {
                    shippingAddressId = c.AddressId,
                    paymentMethod = "COD",
                    lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
                }, idempotencyKey: Guid.NewGuid().ToString("N"));
            }).ToList();

            startGate.SetResult();
            var responses = await Task.WhenAll(tasks);

            var createdCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
            var quotaExceededCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

            // Đúng 5 request thành công, 10 request bị từ chối
            Assert.Equal(5, createdCount);
            Assert.Equal(10, quotaExceededCount);

            // Kiểm tra DB:
            // 1. Quota held đúng bằng 5
            await using var db = await fixture.OpenDatabaseAsync();
            await using var offerCmd = new NpgsqlCommand(
                "SELECT quota_held, quota_sold, quota_total FROM store.flash_sale_offers WHERE id = @id", db);
            offerCmd.Parameters.AddWithValue("id", offerId);
            await using var reader = await offerCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var held = reader.GetInt32(0);
            var sold = reader.GetInt32(1);
            var total = reader.GetInt32(2);
            Assert.Equal(5, held);
            Assert.Equal(0, sold);
            Assert.Equal(5, total);
            await reader.CloseAsync();

            // 2. Đúng 5 allocations HELD
            await using var allocCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM store.flash_sale_allocations WHERE offer_id = @id AND allocation_status = 'HELD'", db);
            allocCmd.Parameters.AddWithValue("id", offerId);
            var allocCount = Convert.ToInt32(await allocCmd.ExecuteScalarAsync());
            Assert.Equal(5, allocCount);

            // Dọn dẹp clients
            foreach (var c in customerClients)
            {
                c.Client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Checkout_accepts_effectively_active_scheduled_campaign_even_before_worker_persists_transition()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (customer, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (customer)
        {
            var addressId = await CreateAddress(customer);
            var (campaignId, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 10, quota: 5);
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var command = new NpgsqlCommand(
                "UPDATE store.flash_sale_campaigns SET campaign_status='SCHEDULED', starts_at=now()-interval '1 minute', ends_at=now()+interval '1 hour' WHERE id=@id", db))
            {
                command.Parameters.AddWithValue("id", campaignId);
                await command.ExecuteNonQueryAsync();
            }

            using var response = await Send(customer, "checkouts", new
            {
                shippingAddressId = addressId, paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task Checkout_waits_for_campaign_cancel_lock_and_cannot_read_stale_active_state()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (customer, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (customer)
        {
            var addressId = await CreateAddress(customer);
            var (campaignId, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 10, quota: 5);
            await using var db = await fixture.OpenDatabaseAsync();
            await using var tx = await db.BeginTransactionAsync();
            await using (var cancel = new NpgsqlCommand(
                "UPDATE store.flash_sale_campaigns SET campaign_status='CANCELLED' WHERE id=@id", db, tx))
            {
                cancel.Parameters.AddWithValue("id", campaignId);
                await cancel.ExecuteNonQueryAsync();
            }

            var checkout = Send(customer, "checkouts", new
            {
                shippingAddressId = addressId, paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            await Task.Delay(200);
            Assert.False(checkout.IsCompleted);
            await tx.CommitAsync();
            using var response = await checkout;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("FLASH_SALE_CAMPAIGN_NOT_ACTIVE",
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Concurrent_checkout_and_cod_confirmation_follow_offer_before_sku_lock_order_without_server_error()
    {
        var (admin, _) = await CreateRoleClient("ADMIN");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (customer1, _) = await CreateRoleClient("CUSTOMER");
        var (customer2, _) = await CreateRoleClient("CUSTOMER");
        using (admin) using (staff) using (customer1) using (customer2)
        {
            var address1 = await CreateAddress(customer1);
            var address2 = await CreateAddress(customer2);
            var (_, offerId, skuId, _, _) = await SetupActiveFlashSale(admin, stock: 20, quota: 10, perCustomerLimit: 3);
            using var first = await Send(customer1, "checkouts", new
            {
                shippingAddressId = address1, paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N"));
            var orderId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirm = Task.Run(async () => { await gate.Task; return await Send(staff, $"staff/orders/{orderId}/confirm-cod", null); });
            var checkout = Task.Run(async () => { await gate.Task; return await Send(customer2, "checkouts", new
            {
                shippingAddressId = address2, paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
            }, idempotencyKey: Guid.NewGuid().ToString("N")); });
            gate.SetResult();
            var responses = await Task.WhenAll(confirm, checkout);
            Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created));
            Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.InternalServerError);
            foreach (var response in responses) response.Dispose();
        }
    }
}
