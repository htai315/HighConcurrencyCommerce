using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ShippingCheckoutTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure shipping checkout 123!";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Completed_legacy_v1_and_v2_idempotency_records_remain_replayable_without_shipping_fields()
    {
        var context = await CreateContextAsync(baseFee: 25_000m, surcharge: 5_000m);
        using var client = context.Client;
        var successKey = "legacy-v1-success";
        var rejectedKey = "legacy-v2-rejected";
        var orderId = Guid.NewGuid();
        var v1Hash = LegacyHash(context, voucherCode: null);
        var v2Hash = LegacyHash(context, voucherCode: "legacy10");
        var successSnapshot = JsonSerializer.Serialize(new
        {
            orderId,
            orderNumber = "HC-LEGACY-REPLAY",
            orderStatus = "PENDING_COD_CONFIRMATION",
            paymentMethod = "COD",
            paymentStatus = "COD_PENDING",
            totalAmount = 200_000m,
            deadlineAt = DateTime.UtcNow.AddHours(1),
            shippingAddress = new
            {
                recipientName = "Legacy", phone = "0900000000", addressLine = "Legacy",
                ward = (string?)null, province = "TP. Hồ Chí Minh"
            },
            items = Array.Empty<object>(),
            subtotalAmount = 200_000m,
            voucherDiscountAmount = 0m,
            voucherCode = (string?)null
        }, WebJson);
        var rejectionSnapshot = JsonSerializer.Serialize(new
        {
            httpStatus = 409,
            code = "OUT_OF_STOCK",
            message = "Legacy rejection"
        }, WebJson);

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO store.checkout_requests(
                customer_id,idempotency_key,payload_hash,request_status,response_snapshot)
            VALUES
                (@customer,@success_key,@v1_hash,'SUCCEEDED',CAST(@success_snapshot AS jsonb)),
                (@customer,@rejected_key,@v2_hash,'REJECTED',CAST(@rejection_snapshot AS jsonb))
            """, db))
        {
            insert.Parameters.AddWithValue("customer", context.UserId);
            insert.Parameters.AddWithValue("success_key", successKey);
            insert.Parameters.AddWithValue("v1_hash", v1Hash);
            insert.Parameters.AddWithValue("success_snapshot", successSnapshot);
            insert.Parameters.AddWithValue("rejected_key", rejectedKey);
            insert.Parameters.AddWithValue("v2_hash", v2Hash);
            insert.Parameters.AddWithValue("rejection_snapshot", rejectionSnapshot);
            await insert.ExecuteNonQueryAsync();
        }

        using var success = await Send(client, LegacyPayload(context, null), successKey);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal("true", success.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(orderId,
            (await success.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid());

        using var rejected = await Send(client, LegacyPayload(context, "legacy10"), rejectedKey);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("true", rejected.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal("OUT_OF_STOCK",
            (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var newMissingShipping = await Send(client, LegacyPayload(context, null), "legacy-new-request");
        Assert.Equal(HttpStatusCode.BadRequest, newMissingShipping.StatusCode);
        Assert.Equal("VALIDATION_ERROR",
            (await newMissingShipping.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Checkout_v3_snapshots_shipping_adds_fee_to_payment_and_replays_old_snapshot()
    {
        var context = await CreateContextAsync(baseFee: 25_000m, surcharge: 5_000m);
        using var client = context.Client;
        var payload = Payload(context, revision: 1, quotedFee: 30_000m);

        using var first = await Send(client, payload, "shipping-snapshot-123");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(230_000m, body.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(30_000m, body.GetProperty("shippingFee").GetDecimal());
        var shipping = body.GetProperty("shippingMethod");
        Assert.Equal(context.MethodId, shipping.GetProperty("shippingMethodId").GetGuid());
        Assert.Equal(context.MethodCode, shipping.GetProperty("code").GetString());
        Assert.Equal("HO CHI MINH", shipping.GetProperty("destinationProvinceKey").GetString());
        var orderId = body.GetProperty("orderId").GetGuid();

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var verify = new NpgsqlCommand("""
            SELECT o.shipping_method_id,o.shipping_method_code_snapshot,o.shipping_method_name_snapshot,
                   o.shipping_fee,o.shipping_method_revision,o.shipping_destination_province_key,
                   o.total_amount,p.expected_amount
            FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id
            WHERE o.id=@order_id
            """, db))
        {
            verify.Parameters.AddWithValue("order_id", orderId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(context.MethodId, reader.GetGuid(0));
            Assert.Equal(context.MethodCode, reader.GetString(1));
            Assert.Equal("Giao hàng v3", reader.GetString(2));
            Assert.Equal(30_000m, reader.GetDecimal(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal("HO CHI MINH", reader.GetString(5));
            Assert.Equal(230_000m, reader.GetDecimal(6));
            Assert.Equal(230_000m, reader.GetDecimal(7));
        }

        await UpdateMethodAsync(context.MethodId, 99_000m, 2);
        using var replay = await Send(client, payload, "shipping-snapshot-123");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30_000m, replayBody.GetProperty("shippingFee").GetDecimal());
        Assert.Equal(orderId, replayBody.GetProperty("orderId").GetGuid());

        using var changedPayload = await Send(client, Payload(context, revision: 2, quotedFee: 104_000m), "shipping-snapshot-123");
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED",
            (await changedPayload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Wrong_quoted_fee_is_rejected_before_inventory_or_order_mutation()
    {
        var context = await CreateContextAsync(baseFee: 20_000m, surcharge: 5_000m);
        using var client = context.Client;
        var before = await ReadStateAsync(context);

        using var response = await Send(client, Payload(context, revision: 1, quotedFee: 20_000m), "shipping-wrong-fee-123");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SHIPPING_QUOTE_CHANGED",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var after = await ReadStateAsync(context);
        Assert.Equal(before.Available, after.Available);
        Assert.Equal(before.Reserved, after.Reserved);
        Assert.Equal(0L, after.OrderCount);
    }

    [Fact]
    public async Task Admin_update_wins_parent_lock_then_checkout_rejects_stale_revision_without_leaks()
    {
        var context = await CreateContextAsync(baseFee: 20_000m, surcharge: 5_000m);
        using var client = context.Client;
        await using var lockConnection = await fixture.OpenDatabaseAsync();
        await using var transaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM store.shipping_methods WHERE id=@id FOR UPDATE", lockConnection, transaction))
        {
            lockCommand.Parameters.AddWithValue("id", context.MethodId);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checkout = Send(client, Payload(context, revision: 1, quotedFee: 25_000m), "shipping-race-123");
        await WaitForBlockedCheckoutAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE store.shipping_methods
            SET base_fee=40000,revision=2,updated_at=now()
            WHERE id=@id
            """, lockConnection, transaction))
        {
            update.Parameters.AddWithValue("id", context.MethodId);
            await update.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();

        using var response = await checkout;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SHIPPING_QUOTE_CHANGED",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var state = await ReadStateAsync(context);
        Assert.Equal(2, state.Available);
        Assert.Equal(0, state.Reserved);
        Assert.Equal(0L, state.OrderCount);
    }

    private async Task WaitForBlockedCheckoutAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        await using var observer = await fixture.OpenDatabaseAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname=current_database()
                  AND cardinality(pg_blocking_pids(pid)) > 0
                  AND query ILIKE '%shipping_methods%FOR SHARE%'
                """, observer);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Checkout did not block on the ShippingMethod parent lock.");
    }

    private async Task<Context> CreateContextAsync(decimal baseFee, decimal surcharge)
    {
        var client = fixture.CreateClient();
        var email = $"shipping_checkout_{Guid.NewGuid():N}@example.invalid";
        using (var register = await SendAuth(client, "auth/register", new
        {
            email, password = Password, confirmPassword = Password, fullName = "Shipping Checkout"
        }))
            Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await fixture.VerifyUserAsync(email);
        using (var login = await SendAuth(client, "auth/login", new { email, password = Password }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var userId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        var methodId = Guid.NewGuid();
        var methodCode = "SHIP_" + methodId.ToString("N")[..12].ToUpperInvariant();
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id FROM store.user_accounts WHERE email=@email;
            """, db);
        command.Parameters.AddWithValue("email", email);
        userId = (Guid)(await command.ExecuteScalarAsync())!;
        await using var seed = new NpgsqlCommand("""
            INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
            VALUES(@address,@user,'Shipping Checkout','0900000030','30 Shipping','Thành phố Hồ Chí Minh',true);
            INSERT INTO store.shipping_methods(
                id,code,name,method_status,base_fee,estimated_min_days,estimated_max_days,
                is_default,display_order,revision,created_by,updated_by)
            VALUES(@method,@method_code,'Giao hàng v3','ACTIVE',@base_fee,2,4,false,20,1,
                   '00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000002');
            INSERT INTO store.shipping_method_province_rates(
                shipping_method_id,province_key,surcharge_fee,is_active,created_by,updated_by)
            VALUES(@method,'HO CHI MINH',@surcharge,true,
                   '00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000002');
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Sản phẩm shipping v3','PUBLISHED',@user,@user,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@sku_code,'Default',200000,'ACTIVE');
            INSERT INTO store.inventory_balances(product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(@sku,2,2,0,0,1);
            """, db);
        seed.Parameters.AddWithValue("address", addressId);
        seed.Parameters.AddWithValue("user", userId);
        seed.Parameters.AddWithValue("method", methodId);
        seed.Parameters.AddWithValue("method_code", methodCode);
        seed.Parameters.AddWithValue("base_fee", baseFee);
        seed.Parameters.AddWithValue("surcharge", surcharge);
        seed.Parameters.AddWithValue("product", productId);
        seed.Parameters.AddWithValue("sku", skuId);
        seed.Parameters.AddWithValue("sku_code", "SHIP-" + skuId.ToString("N")[..10].ToUpperInvariant());
        await seed.ExecuteNonQueryAsync();
        return new Context(client, userId, addressId, methodId, methodCode, skuId);
    }

    private static object Payload(Context context, long revision, decimal quotedFee) => new
    {
        shippingAddressId = context.AddressId,
        shippingMethodId = context.MethodId,
        shippingMethodRevision = revision,
        quotedShippingFee = quotedFee,
        paymentMethod = "COD",
        lines = new[] { new { productSkuId = context.SkuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
    };

    private static object LegacyPayload(Context context, string? voucherCode) => new
    {
        voucherCode,
        shippingAddressId = context.AddressId,
        paymentMethod = "COD",
        lines = new[] { new { productSkuId = context.SkuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
    };

    private static string LegacyHash(Context context, string? voucherCode)
    {
        object canonical = voucherCode is null
            ? new
            {
                version = "checkout-v1",
                shippingAddressId = context.AddressId.ToString("D").ToLowerInvariant(),
                paymentMethod = "COD",
                lines = new[]
                {
                    new
                    {
                        productSkuId = context.SkuId.ToString("D").ToLowerInvariant(),
                        quantity = 1,
                        flashSaleOfferId = (string?)null
                    }
                }
            }
            : new
            {
                version = "checkout-v2",
                voucherCode = voucherCode.Trim().ToUpperInvariant(),
                shippingAddressId = context.AddressId.ToString("D").ToLowerInvariant(),
                paymentMethod = "COD",
                lines = new[]
                {
                    new
                    {
                        productSkuId = context.SkuId.ToString("D").ToLowerInvariant(),
                        quantity = 1,
                        flashSaleOfferId = (string?)null
                    }
                }
            };
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(canonical, WebJson)));
    }

    private async Task<(int Available, int Reserved, long OrderCount)> ReadStateAsync(Context context)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT available_qty,reserved_qty FROM store.inventory_balances WHERE product_sku_id=@sku;
            SELECT count(*) FROM store.sales_orders WHERE customer_id=@user;
            """, db);
        command.Parameters.AddWithValue("sku", context.SkuId);
        command.Parameters.AddWithValue("user", context.UserId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var available = reader.GetInt32(0);
        var reserved = reader.GetInt32(1);
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        return (available, reserved, reader.GetInt64(0));
    }

    private async Task UpdateMethodAsync(Guid methodId, decimal baseFee, long revision)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE store.shipping_methods SET base_fee=@fee,revision=@revision,updated_at=now() WHERE id=@id", db);
        command.Parameters.AddWithValue("fee", baseFee);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("id", methodId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, object body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkouts")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAuth(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
    }

    private sealed record Context(HttpClient Client, Guid UserId, Guid AddressId, Guid MethodId, string MethodCode, Guid SkuId);
}
