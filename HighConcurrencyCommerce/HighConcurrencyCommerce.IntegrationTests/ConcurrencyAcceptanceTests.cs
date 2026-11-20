using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ConcurrencyAcceptanceTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private sealed record Buyer(HttpClient Client, Guid AddressId);

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Checkout(
        HttpClient client, Guid addressId, Guid skuId, string idempotencyKey, string csrfToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkouts")
        {
            Content = JsonContent.Create(new
            {
                shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task WaitForBlockedQueries(
        CatalogFixture fixture, string? queryFragment, int minimumCount, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        await using var observer = await fixture.OpenDatabaseAsync();
        while (DateTime.UtcNow < deadline)
        {
            var sql = """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND cardinality(pg_blocking_pids(pid)) > 0
                """;
            if (queryFragment is not null) sql += " AND query ILIKE @fragment";
            await using var command = new NpgsqlCommand(sql, observer);
            if (queryFragment is not null)
                command.Parameters.AddWithValue("fragment", $"%{queryFragment}%");
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) >= minimumCount) return;
            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Expected at least {minimumCount} blocked PostgreSQL queries"
            + (queryFragment is null ? "." : $" containing '{queryFragment}'."));
    }

    private static async Task WaitUntilDatabaseTimePasses(CatalogFixture fixture, DateTime instant)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        await using var observer = await fixture.OpenDatabaseAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT clock_timestamp() >= @instant", observer);
            command.Parameters.AddWithValue("instant", instant);
            if (await command.ExecuteScalarAsync() is true) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("PostgreSQL clock did not pass the expected instant within the test deadline.");
    }

    [Fact]
    public async Task AC20a_one_hundred_concurrent_buyers_competing_for_ten_units_never_oversell()
    {
        const int buyerCount = 100;
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCommand = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='CUSTOMER'", db);
        var customerRoleId = (Guid)(await roleCommand.ExecuteScalarAsync())!;
        var buyerSeeds = Enumerable.Range(0, buyerCount).Select(_ => new
        {
            UserId = Guid.NewGuid(), AddressId = Guid.NewGuid(), Token = CreateToken()
        }).ToArray();
        await using (var tx = await db.BeginTransactionAsync())
        {
            foreach (var (seed, index) in buyerSeeds.Select((value, index) => (value, index)))
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,email_verified_at)
                    VALUES(@user,@role,@email,'acceptance-test-no-login','AC20a Buyer','ACTIVE',now());
                    INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
                    VALUES(@address,@user,'Người nhận AC20a','0901234567','Sân cầu concurrency','TP Hồ Chí Minh',true);
                    INSERT INTO store.auth_sessions(token_hash,user_id,created_at,expires_at)
                    VALUES(@hash,@user,now(),now()+interval '1 day');
                    """, db, tx);
                command.Parameters.AddWithValue("user", seed.UserId);
                command.Parameters.AddWithValue("role", customerRoleId);
                command.Parameters.AddWithValue("email", $"ac20a_{index}_{Guid.NewGuid():N}@example.invalid");
                command.Parameters.AddWithValue("address", seed.AddressId);
                command.Parameters.AddWithValue("hash", HashToken(seed.Token));
                await command.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using (var seedSku = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Vợt AC20a','PUBLISHED',@actor,@actor,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@code,'4U G5',500000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,10,0,0,0);
            """, db))
        {
            seedSku.Parameters.AddWithValue("product", productId);
            seedSku.Parameters.AddWithValue("sku", skuId);
            seedSku.Parameters.AddWithValue("actor", buyerSeeds[0].UserId);
            seedSku.Parameters.AddWithValue("code", "AC20A-" + skuId.ToString("N")[..12]);
            await seedSku.ExecuteNonQueryAsync();
        }

        using WebApplicationFactory<Program> app = fixture.CreateConcurrentApplication();
        var buyers = buyerSeeds.Select(seed =>
        {
            var client = app.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
            });
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"__Host-hcc-session={seed.Token}");
            return new Buyer(client, seed.AddressId);
        }).ToArray();

        try
        {
            var csrfTokens = await Task.WhenAll(buyers.Select(buyer => Csrf(buyer.Client)));
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkoutTasks = buyers.Select(async (buyer, index) =>
            {
                await startGate.Task;
                return await Checkout(buyer.Client, buyer.AddressId, skuId,
                    $"ac20a-{index:D3}-{Guid.NewGuid():N}", csrfTokens[index]);
            }).ToArray();
            startGate.SetResult();
            var responses = await Task.WhenAll(checkoutTasks);
            Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
            Assert.Equal(90, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var rejected in responses.Where(x => x.StatusCode == HttpStatusCode.Conflict))
            {
                var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("OUT_OF_STOCK", body.GetProperty("code").GetString());
            }
            foreach (var response in responses) response.Dispose();
        }
        finally
        {
            foreach (var buyer in buyers) buyer.Client.Dispose();
        }

        await using var verify = new NpgsqlCommand("""
            SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku;
            SELECT count(*) FROM store.sales_order_items WHERE product_sku_id=@sku;
            SELECT count(*) FROM store.inventory_movements WHERE product_sku_id=@sku AND movement_type='RESERVE';
            SELECT count(*) FILTER (WHERE request_status='SUCCEEDED'),count(*) FILTER (WHERE request_status='REJECTED')
            FROM store.checkout_requests WHERE idempotency_key LIKE 'ac20a-%';
            """, db);
        verify.Parameters.AddWithValue("sku", skuId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(10, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(10L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(10L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
        Assert.Equal(10L, reader.GetInt64(0)); Assert.Equal(90L, reader.GetInt64(1));
    }

    [Fact]
    public async Task F01_Concurrent_cancel_order_and_checkout_same_sku_do_not_deadlock()
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='CUSTOMER'", db);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        var userId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        var token = CreateToken();

        await using (var seedUser = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,email_verified_at)
            VALUES(@user,@role,@email,'f01-test','F01 Customer','ACTIVE',now());
            INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
            VALUES(@address,@user,'Người nhận F01','0901234567','123 Concurrency St','TP Hồ Chí Minh',true);
            INSERT INTO store.auth_sessions(token_hash,user_id,created_at,expires_at)
            VALUES(@hash,@user,now(),now()+interval '1 day');
            """, db))
        {
            seedUser.Parameters.AddWithValue("user", userId);
            seedUser.Parameters.AddWithValue("role", roleId);
            seedUser.Parameters.AddWithValue("email", $"f01_{Guid.NewGuid():N}@example.invalid");
            seedUser.Parameters.AddWithValue("address", addressId);
            seedUser.Parameters.AddWithValue("hash", HashToken(token));
            await seedUser.ExecuteNonQueryAsync();
        }

        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using (var seedSku = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Vợt F01','PUBLISHED',@actor,@actor,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@code,'4U G5',500000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,10,0,0,0);
            """, db))
        {
            seedSku.Parameters.AddWithValue("product", productId);
            seedSku.Parameters.AddWithValue("sku", skuId);
            seedSku.Parameters.AddWithValue("actor", userId);
            seedSku.Parameters.AddWithValue("code", "F01-" + skuId.ToString("N")[..12]);
            await seedSku.ExecuteNonQueryAsync();
        }

        using WebApplicationFactory<Program> app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"__Host-hcc-session={token}");
        var csrf = await Csrf(client);

        // 1. Create Order 1 with SKU
        using var order1Res = await Checkout(client, addressId, skuId, $"f01-order1-{Guid.NewGuid():N}", csrf);
        Assert.Equal(HttpStatusCode.Created, order1Res.StatusCode);
        var order1Id = (await order1Res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

        // 2. Concurrently cancel Order 1 and checkout Order 2 with the same SKU under explicit lock contention
        await using (var holdTx = await db.BeginTransactionAsync())
        {
            await using (var lockCmd = new NpgsqlCommand(
                "SELECT id FROM store.inventory_balances WHERE product_sku_id = @sku FOR UPDATE", db, holdTx))
            {
                lockCmd.Parameters.AddWithValue("sku", skuId);
                await lockCmd.ExecuteNonQueryAsync();
            }

            var cancelTask = Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order1Id}/cancel")
                {
                    Content = JsonContent.Create(new { reason = "Hủy race F01" })
                };
                req.Headers.Add("X-CSRF-TOKEN", csrf);
                return await client.SendAsync(req);
            });

            var checkoutTask = Task.Run(async () =>
            {
                return await Checkout(client, addressId, skuId, $"f01-order2-{Guid.NewGuid():N}", csrf);
            });

            await WaitForBlockedQueries(fixture, queryFragment: null, minimumCount: 2);
            await holdTx.CommitAsync();

            var results = await Task.WhenAll(cancelTask, checkoutTask);
            using var cancelRes = results[0];
            using var checkoutRes = results[1];

            // Neither request must fail with 500 (No 40P01 Deadlock exception escaping!)
            Assert.NotEqual(HttpStatusCode.InternalServerError, cancelRes.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, checkoutRes.StatusCode);

            // Cancel of Order 1 succeeds
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);
            // Checkout of Order 2 succeeds
            Assert.Equal(HttpStatusCode.Created, checkoutRes.StatusCode);
        }
    }

    [Fact]
    public async Task F02_Concurrent_delete_address_and_checkout_serialize_cleanly()
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='CUSTOMER'", db);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        var userId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        var token = CreateToken();

        await using (var seedUser = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,email_verified_at)
            VALUES(@user,@role,@email,'f02-test','F02 Customer','ACTIVE',now());
            INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
            VALUES(@address,@user,'Người nhận F02','0901234567','123 Concurrency St','TP Hồ Chí Minh',true);
            INSERT INTO store.auth_sessions(token_hash,user_id,created_at,expires_at)
            VALUES(@hash,@user,now(),now()+interval '1 day');
            """, db))
        {
            seedUser.Parameters.AddWithValue("user", userId);
            seedUser.Parameters.AddWithValue("role", roleId);
            seedUser.Parameters.AddWithValue("email", $"f02_{Guid.NewGuid():N}@example.invalid");
            seedUser.Parameters.AddWithValue("address", addressId);
            seedUser.Parameters.AddWithValue("hash", HashToken(token));
            await seedUser.ExecuteNonQueryAsync();
        }

        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using (var seedSku = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Vợt F02','PUBLISHED',@actor,@actor,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@code,'4U G5',500000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,10,0,0,0);
            """, db))
        {
            seedSku.Parameters.AddWithValue("product", productId);
            seedSku.Parameters.AddWithValue("sku", skuId);
            seedSku.Parameters.AddWithValue("actor", userId);
            seedSku.Parameters.AddWithValue("code", "F02-" + skuId.ToString("N")[..12]);
            await seedSku.ExecuteNonQueryAsync();
        }

        using WebApplicationFactory<Program> app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"__Host-hcc-session={token}");
        var csrf = await Csrf(client);

        // Force both requests to queue on user lock and serialize cleanly
        await using (var holdTx = await db.BeginTransactionAsync())
        {
            await using (var lockCmd = new NpgsqlCommand(
                "SELECT id FROM store.user_accounts WHERE id = @user FOR NO KEY UPDATE", db, holdTx))
            {
                lockCmd.Parameters.AddWithValue("user", userId);
                await lockCmd.ExecuteNonQueryAsync();
            }

            var checkoutTask = Task.Run(async () =>
            {
                return await Checkout(client, addressId, skuId, $"f02-checkout-{Guid.NewGuid():N}", csrf);
            });

            var deleteTask = Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/account/addresses/{addressId}");
                req.Headers.Add("X-CSRF-TOKEN", csrf);
                return await client.SendAsync(req);
            });

            await WaitForBlockedQueries(fixture, "user_accounts", minimumCount: 2);
            await holdTx.CommitAsync();

            var results = await Task.WhenAll(checkoutTask, deleteTask);
            using var checkoutRes = results[0];
            using var deleteRes = results[1];

            // Crucial invariant: NEITHER operation must crash with 500 (No sales_orders_shipping_address_id_fkey FK violation!)
            Assert.NotEqual(HttpStatusCode.InternalServerError, checkoutRes.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, deleteRes.StatusCode);

            if (checkoutRes.StatusCode == HttpStatusCode.Created)
            {
                // Checkout won first -> Address deletion was blocked with 409 ADDRESS_IN_USE
                Assert.Equal(HttpStatusCode.Conflict, deleteRes.StatusCode);
                var prob = await deleteRes.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("ADDRESS_IN_USE", prob.GetProperty("code").GetString());
            }
            else
            {
                // Delete won first (204) -> Checkout safely failed with 404 ADDRESS_NOT_FOUND
                Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, checkoutRes.StatusCode);
                var prob = await checkoutRes.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("ADDRESS_NOT_FOUND", prob.GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task F05_Checkout_with_expired_flash_sale_is_rejected()
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='CUSTOMER'", db);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        var userId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        var token = CreateToken();

        await using (var seedUser = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,email_verified_at)
            VALUES(@user,@role,@email,'f05-test','F05 Customer','ACTIVE',now());
            INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
            VALUES(@address,@user,'Người nhận F05','0901234567','123 Concurrency St','TP Hồ Chí Minh',true);
            INSERT INTO store.auth_sessions(token_hash,user_id,created_at,expires_at)
            VALUES(@hash,@user,now(),now()+interval '1 day');
            """, db))
        {
            seedUser.Parameters.AddWithValue("user", userId);
            seedUser.Parameters.AddWithValue("role", roleId);
            seedUser.Parameters.AddWithValue("email", $"f05_{Guid.NewGuid():N}@example.invalid");
            seedUser.Parameters.AddWithValue("address", addressId);
            seedUser.Parameters.AddWithValue("hash", HashToken(token));
            await seedUser.ExecuteNonQueryAsync();
        }

        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using (var seedSku = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Vợt F05','PUBLISHED',@actor,@actor,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@code,'4U G5',500000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,10,0,0,0);
            """, db))
        {
            seedSku.Parameters.AddWithValue("product", productId);
            seedSku.Parameters.AddWithValue("sku", skuId);
            seedSku.Parameters.AddWithValue("actor", userId);
            seedSku.Parameters.AddWithValue("code", "F05-" + skuId.ToString("N")[..12]);
            await seedSku.ExecuteNonQueryAsync();
        }

        // Start with a comfortably active campaign. The test moves ends_at only after
        // proving that checkout is blocked on the campaign row.
        var campaignId = Guid.NewGuid();
        var offerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var seedSale = new NpgsqlCommand("""
            INSERT INTO store.flash_sale_campaigns(id,name,campaign_status,starts_at,ends_at,created_by)
            VALUES(@camp,'Ending Sale','ACTIVE',@starts,@ends,@actor);
            INSERT INTO store.flash_sale_offers(id,campaign_id,product_sku_id,sale_price,quota_total,per_customer_limit)
            VALUES(@offer,@camp,@sku,300000,5,2);
            """, db))
        {
            seedSale.Parameters.AddWithValue("camp", campaignId);
            seedSale.Parameters.AddWithValue("offer", offerId);
            seedSale.Parameters.AddWithValue("sku", skuId);
            seedSale.Parameters.AddWithValue("starts", now.AddMinutes(-10));
            seedSale.Parameters.AddWithValue("ends", now.AddMinutes(10));
            seedSale.Parameters.AddWithValue("actor", userId);
            await seedSale.ExecuteNonQueryAsync();
        }

        using WebApplicationFactory<Program> app = fixture.CreateConcurrentApplication();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"__Host-hcc-session={token}");
        var csrf = await Csrf(client);

        // Checkout begins while campaign is active, but waits on locked campaign row until AFTER ends_at
        await using (var holdTx = await db.BeginTransactionAsync())
        {
            await using (var lockCmd = new NpgsqlCommand(
                "SELECT id FROM store.flash_sale_campaigns WHERE id = @camp FOR UPDATE", db, holdTx))
            {
                lockCmd.Parameters.AddWithValue("camp", campaignId);
                await lockCmd.ExecuteNonQueryAsync();
            }

            var checkoutTask = Task.Run(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkouts")
                {
                    Content = JsonContent.Create(new
                    {
                        shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
                        paymentMethod = "COD",
                        lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)offerId } }
                    })
                };
                request.Headers.Add("X-CSRF-TOKEN", csrf);
                request.Headers.Add("Idempotency-Key", $"f05-{Guid.NewGuid():N}");
                return await client.SendAsync(request);
            });

            await WaitForBlockedQueries(fixture, "flash_sale_campaigns", minimumCount: 1);

            var expiresAt = DateTime.UtcNow.AddMilliseconds(200);
            await using (var expireCmd = new NpgsqlCommand("""
                UPDATE store.flash_sale_campaigns
                SET ends_at = @expires
                WHERE id = @campaign
                """, db, holdTx))
            {
                expireCmd.Parameters.AddWithValue("campaign", campaignId);
                expireCmd.Parameters.AddWithValue("expires", expiresAt);
                await expireCmd.ExecuteNonQueryAsync();
            }

            await WaitUntilDatabaseTimePasses(fixture, expiresAt);

            // Release the hold lock; checkout acquires lock and re-checks expiration under lock
            await holdTx.CommitAsync();

            using var response = await checkoutTask;

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("FLASH_SALE_CAMPAIGN_NOT_ACTIVE", problem.GetProperty("code").GetString());
        }
    }
}
