using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using HighConcurrencyCommerce.Infrastructure.Returns;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class OrderReturnLifecycleTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure returns pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string path, object? body = null, string? key = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AddressId)> CreateCustomer(string? prefix = null)
    {
        var p = prefix ?? "retcust";
        var email = $"{p}_{Guid.NewGuid():N}@example.invalid";
        var client = fixture.CreateClient();
        using var register = await Send(client, "auth/register",
            new { email, password = Password, confirmPassword = Password, fullName = "Return Customer" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await fixture.VerifyUserAsync(email);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var addressResponse = await Send(client, "account/addresses", new
        {
            recipientName = "Người nhận trả hàng",
            phone = "0901234567",
            addressLine = "123 Đường Trả Hàng",
            province = "Hà Nội",
            isDefault = true
        });
        var address = await addressResponse.Content.ReadFromJsonAsync<JsonElement>();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE email=@email", db);
        query.Parameters.AddWithValue("email", email);
        return (client, (Guid)(await query.ExecuteScalarAsync())!, Guid.Parse(address.GetProperty("id").GetString()!));
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleClient(string roleKey)
    {
        var id = Guid.NewGuid();
        var email = $"ret_{roleKey.ToLowerInvariant()}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key=@role", db);
        role.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,email_verified_at) VALUES(@id,@role,@email,@hash,@name,'ACTIVE',now())", db);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("name", roleKey + " User");
        await insert.ExecuteNonQueryAsync();
        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, id);
    }

    private async Task<(Guid ProductId, Guid SkuId)> CreateProductAndSku(Guid actorId, int available = 10, decimal price = 200_000m)
    {
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at) VALUES(@product,@title,'PUBLISHED',@actor,@actor,now()); " +
            "INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) VALUES(@sku,@product,@code,'Size L',@price,'ACTIVE'); " +
            "INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision) VALUES(gen_random_uuid(),@sku,@available,@available,0,0,0);", db);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("sku", skuId);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("title", "Sản phẩm trả hàng " + productId.ToString("N")[..8]);
        command.Parameters.AddWithValue("code", "RET-" + skuId.ToString("N")[..10].ToUpperInvariant());
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        return (productId, skuId);
    }

    private async Task<(Guid OrderId, Guid ShipmentId, Guid ProductId, Guid SkuId, Guid OrderItemId, decimal TotalAmount)> PrepareDeliveredCodOrder(
        HttpClient customer, HttpClient staff, Guid customerId, Guid addressId, int quantity = 2, decimal unitPrice = 200_000m)
    {
        var (productId, skuId) = await CreateProductAndSku(customerId, available: 10, price: unitPrice);
        var prepared = await PrepareDeliveredCodOrderForSku(customer, staff, addressId, productId, skuId, quantity, unitPrice);
        return (prepared.OrderId, prepared.ShipmentId, productId, skuId, prepared.OrderItemId, prepared.TotalAmount);
    }

    private async Task<(Guid OrderId, Guid ShipmentId, Guid OrderItemId, decimal TotalAmount)> PrepareDeliveredCodOrderForSku(
        HttpClient customer, HttpClient staff, Guid addressId, Guid productId, Guid skuId, int quantity = 1, decimal unitPrice = 200_000m)
    {
        using var checkout = await Send(customer, "checkouts", new
        {
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity, flashSaleOfferId = (Guid?)null } }
        }, "return-order-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var checkoutJson = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = Guid.Parse(checkoutJson.GetProperty("orderId").GetString()!);

        // Staff confirms COD
        using var confirm = await Send(staff, $"staff/orders/{orderId}/confirm-cod");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // Staff starts fulfillment & packs
        using var start = await Send(staff, $"staff/orders/{orderId}/start-fulfillment");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var shipmentId = Guid.Parse((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shipmentId").GetString()!);

        using var pack = await Send(staff, $"staff/shipments/{shipmentId}/pack");
        Assert.Equal(HttpStatusCode.OK, pack.StatusCode);

        // Staff handovers to shipping
        using var handover = await Send(staff, $"staff/shipments/{shipmentId}/handover");
        Assert.Equal(HttpStatusCode.OK, handover.StatusCode);

        // Staff delivers successfully
        var expectedAmount = quantity * unitPrice;
        using var delivered = await Send(staff, $"staff/shipments/{shipmentId}/delivery-result",
            new { result = "DELIVERED", codCollectedAmount = expectedAmount, collectionReference = "COD-COLL-" + Guid.NewGuid().ToString("N")[..8] });
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        // Query OrderItem ID
        await using var db = await fixture.OpenDatabaseAsync();
        await using var itemQuery = new NpgsqlCommand("SELECT id FROM store.sales_order_items WHERE order_id=@orderId LIMIT 1", db);
        itemQuery.Parameters.AddWithValue("orderId", orderId);
        var orderItemId = (Guid)(await itemQuery.ExecuteScalarAsync())!;

        return (orderId, shipmentId, orderItemId, expectedAmount);
    }

    private async Task<(Guid OrderId, Guid ReturnId, Guid ReturnLineId, Guid ExchangeShipmentId, Guid TargetSkuId)> PrepareExchangeReady(
        HttpClient customer, HttpClient staff, HttpClient admin, Guid customerId, Guid addressId, int targetAvailable = 2)
    {
        var (productId, sourceSkuId) = await CreateProductAndSku(customerId, available: 10);
        var targetSkuId = Guid.NewGuid();
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@target,@product,@code,'Size XL',200000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@target,@available,@available,0,0,0);
            """, db))
        {
            insert.Parameters.AddWithValue("target", targetSkuId);
            insert.Parameters.AddWithValue("product", productId);
            insert.Parameters.AddWithValue("code", "EX-" + targetSkuId.ToString("N")[..10].ToUpperInvariant());
            insert.Parameters.AddWithValue("available", targetAvailable);
            await insert.ExecuteNonQueryAsync();
        }

        var delivered = await PrepareDeliveredCodOrderForSku(customer, staff, addressId, productId, sourceSkuId);
        using var create = await Send(customer, $"orders/{delivered.OrderId}/returns", new
        {
            resolutionType = "EXCHANGE", reasonCode = "WRONG_SIZE", customerNote = "Đổi sang XL",
            items = new[] { new { orderItemId = delivered.OrderItemId, requestedQuantity = 1, targetSkuId } }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var returnId = Guid.Parse(created.GetProperty("id").GetString()!);
        var lineId = Guid.Parse(created.GetProperty("lines")[0].GetProperty("id").GetString()!);
        using var approve = await Send(admin, $"admin/returns/{returnId}/approve", new { decisionNote = "Duyệt đổi" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var receive = await Send(staff, $"staff/returns/{returnId}/receive", new { receivedEvidence = "EX-READY" });
        Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
        using var inspect = await Send(staff, $"staff/returns/{returnId}/inspect", new
        {
            lines = new[] { new { returnLineId = lineId, restockQty = 1, quarantineQty = 0, damagedQty = 0, scrappedQty = 0 } },
            inspectionNote = "Đạt kiểm định"
        });
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        var inspected = await inspect.Content.ReadFromJsonAsync<JsonElement>();
        var shipmentId = Guid.Parse(inspected.GetProperty("exchangeShipment").GetProperty("id").GetString()!);
        return (delivered.OrderId, returnId, lineId, shipmentId, targetSkuId);
    }

    [Fact]
    public async Task Direct_exchange_reserves_allocates_and_delivers_replacement_without_changing_payment()
    {
        var (customer, customerId, addressId) = await CreateCustomer("exchange");
        var (staff, staffId) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            var (orderId, _, productId, _, orderItemId, _) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId, 1);
            var targetSkuId = Guid.NewGuid();
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
                VALUES(@id,@product,'EX-TARGET-' || left(@id::text,8),'Size XL',200000,'ACTIVE');
                INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
                VALUES(gen_random_uuid(),@id,1,1,0,0,0);
                """, db))
            {
                command.Parameters.AddWithValue("id", targetSkuId);
                command.Parameters.AddWithValue("product", productId);
                await command.ExecuteNonQueryAsync();
            }

            var eligibility = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{orderId}/return-eligibility");
            Assert.Contains(eligibility.GetProperty("items")[0].GetProperty("exchangeTargets").EnumerateArray(), x => Guid.Parse(x.GetProperty("skuId").GetString()!) == targetSkuId);

            using var create = await Send(customer, $"orders/{orderId}/returns", new
            {
                resolutionType = "EXCHANGE", reasonCode = "WRONG_SIZE", customerNote = "Cần đổi size XL",
                items = new[] { new { orderItemId, requestedQuantity = 1, targetSkuId } }
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<JsonElement>();
            var returnId = Guid.Parse(created.GetProperty("id").GetString()!);
            Assert.Equal("EXCHANGE", created.GetProperty("resolutionType").GetString());

            using var approve = await Send(admin, $"admin/returns/{returnId}/approve", new { decisionNote = "Đủ điều kiện" });
            Assert.True(approve.StatusCode == HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var check = new NpgsqlCommand("SELECT available_qty,reserved_qty FROM store.inventory_balances WHERE product_sku_id=@id", db))
            {
                check.Parameters.AddWithValue("id", targetSkuId);
                await using var reader = await check.ExecuteReaderAsync(); await reader.ReadAsync();
                Assert.Equal(0, reader.GetInt32(0)); Assert.Equal(1, reader.GetInt32(1));
            }

            using var receive = await Send(staff, $"staff/returns/{returnId}/receive", new { receivedEvidence = "EX-RCV-001" });
            Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
            var receiveJson = await receive.Content.ReadFromJsonAsync<JsonElement>();
            var lineId = Guid.Parse(receiveJson.GetProperty("lines")[0].GetProperty("id").GetString()!);
            using var inspect = await Send(staff, $"staff/returns/{returnId}/inspect", new
            {
                lines = new[] { new { returnLineId = lineId, restockQty = 1, quarantineQty = 0, damagedQty = 0, scrappedQty = 0 } },
                inspectionNote = "Đủ điều kiện đổi"
            });
            Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
            var inspected = await inspect.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("EXCHANGE_READY", inspected.GetProperty("returnStatus").GetString());
            var exchangeShipmentId = Guid.Parse(inspected.GetProperty("exchangeShipment").GetProperty("id").GetString()!);

            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{exchangeShipmentId}/pack")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{exchangeShipmentId}/handover")).StatusCode);
            using var delivered = await Send(staff, $"staff/exchange-shipments/{exchangeShipmentId}/delivery-result", new { result = "DELIVERED" });
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            var deliveredJson = await delivered.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("EXCHANGED", deliveredJson.GetProperty("returnStatus").GetString());

            await using var finalDb = await fixture.OpenDatabaseAsync();
            await using var payment = new NpgsqlCommand("SELECT payment_status FROM store.payments WHERE order_id=@id", finalDb);
            payment.Parameters.AddWithValue("id", orderId);
            Assert.Equal("COD_COLLECTED", (string)(await payment.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Exchange_inspection_and_concurrent_handover_are_exactly_once()
    {
        var (customer, customerId, addressId) = await CreateCustomer("exchange_once");
        var (staff, staffId) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            var ready = await PrepareExchangeReady(customer, staff, admin, customerId, addressId, 1);
            var replayPayload = new
            {
                lines = new[] { new { returnLineId = ready.ReturnLineId, restockQty = 1, quarantineQty = 0, damagedQty = 0, scrappedQty = 0 } },
                inspectionNote = "Đạt kiểm định"
            };
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/returns/{ready.ReturnId}/inspect", replayPayload)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/pack")).StatusCode);

            using var carrierResponse = await Send(admin, "admin/carriers", new
            {
                code = "EX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
                name = "Exchange Carrier",
                trackingUrlTemplate = "https://tracking.example.com/{trackingNumber}"
            });
            Assert.Equal(HttpStatusCode.Created, carrierResponse.StatusCode);
            var carrierId = (await carrierResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK,
                (await Send(admin, $"admin/carriers/{carrierId}/activate", new { expectedRevision = 1L })).StatusCode);
            using var consignmentResponse = await Send(staff,
                $"staff/carrier-consignments/exchange-shipments/{ready.ExchangeShipmentId}", new
                {
                    carrierId, trackingNumber = "EXCHANGE-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant()
                });
            Assert.Equal(HttpStatusCode.Created, consignmentResponse.StatusCode);
            var consignmentId = (await consignmentResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            using (var labelRequest = new HttpRequestMessage(HttpMethod.Post,
                       $"/api/v1/staff/carrier-consignments/{consignmentId}/label"))
            {
                var multipart = new MultipartFormDataContent();
                var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7\nexchange label"));
                file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                multipart.Add(file, "file", "exchange.pdf");
                multipart.Add(new StringContent("1"), "expectedRevision");
                labelRequest.Content = multipart;
                labelRequest.Headers.Add("X-CSRF-TOKEN", await Csrf(staff));
                using var labelResponse = await staff.SendAsync(labelRequest);
                Assert.Equal(HttpStatusCode.OK, labelResponse.StatusCode);
            }

            var handovers = await Task.WhenAll(
                Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/handover"),
                Send(staff, $"staff/carrier-consignments/{consignmentId}/handover", new { expectedRevision = 2L }));
            Assert.All(handovers, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            foreach (var response in handovers) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var command = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM store.exchange_shipments WHERE return_request_id=@return),
                  (SELECT count(*) FROM store.inventory_movements WHERE source_id=@return AND movement_type='EXCHANGE_STOCK_OUT'),
                  (SELECT physical_qty FROM store.inventory_balances WHERE product_sku_id=@target),
                  (SELECT allocated_qty FROM store.inventory_balances WHERE product_sku_id=@target),
                  (SELECT count(*) FROM store.exchange_delivery_attempts WHERE exchange_shipment_id=@shipment AND attempt_no=1),
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@consignment AND event_type='HANDED_OVER'),
                  (SELECT consignment_status FROM store.carrier_consignments WHERE id=@consignment)
                """, db);
            command.Parameters.AddWithValue("return", ready.ReturnId);
            command.Parameters.AddWithValue("target", ready.TargetSkuId);
            command.Parameters.AddWithValue("shipment", ready.ExchangeShipmentId);
            command.Parameters.AddWithValue("consignment", consignmentId);
            await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal(1L, reader.GetInt64(5));
            Assert.Equal("HANDED_OVER", reader.GetString(6));
            await reader.DisposeAsync();

            using var delivered = await Send(staff,
                $"staff/exchange-shipments/{ready.ExchangeShipmentId}/delivery-result",
                new { result = "DELIVERED" });
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            await using var deliveredCheck = new NpgsqlCommand("""
                SELECT consignment_status,delivered_at IS NOT NULL,
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@id AND event_type='DELIVERED')
                FROM store.carrier_consignments WHERE id=@id
                """, db);
            deliveredCheck.Parameters.AddWithValue("id", consignmentId);
            await using var deliveredReader = await deliveredCheck.ExecuteReaderAsync();
            Assert.True(await deliveredReader.ReadAsync());
            Assert.Equal("DELIVERED", deliveredReader.GetString(0));
            Assert.True(deliveredReader.GetBoolean(1));
            Assert.Equal(1L, deliveredReader.GetInt64(2));
        }
    }

    [Fact]
    public async Task Exchange_delivery_retry_limit_and_return_stock_are_idempotent()
    {
        var (customer, customerId, addressId) = await CreateCustomer("exchange_retry");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            var ready = await PrepareExchangeReady(customer, staff, admin, customerId, addressId, 1);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/pack")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/handover")).StatusCode);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/delivery-result", new { result = "FAILED", note = $"Fail {attempt}" })).StatusCode);
                if (attempt < 3)
                    Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/retry")).StatusCode);
            }
            Assert.Equal(HttpStatusCode.Conflict, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/retry")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/return-stock", new { evidence = "RETURN-EX-001" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/exchange-shipments/{ready.ExchangeShipmentId}/return-stock", new { evidence = "RETURN-EX-001" })).StatusCode);

            await using var db = await fixture.OpenDatabaseAsync();
            await using var command = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM store.exchange_delivery_attempts WHERE exchange_shipment_id=@shipment),
                  (SELECT count(*) FROM store.inventory_movements WHERE source_id=@return AND movement_type='EXCHANGE_RETURN_IN'),
                  physical_qty, quarantined_qty
                FROM store.inventory_balances WHERE product_sku_id=@target
                """, db);
            command.Parameters.AddWithValue("shipment", ready.ExchangeShipmentId);
            command.Parameters.AddWithValue("return", ready.ReturnId);
            command.Parameters.AddWithValue("target", ready.TargetSkuId);
            await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
        }
    }

    [Fact]
    public async Task Concurrent_exchange_approvals_compete_for_last_target_unit_without_leak()
    {
        var (customerA, customerAId, addressA) = await CreateCustomer("exchange_slot_a");
        var (customerB, _, addressB) = await CreateCustomer("exchange_slot_b");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (adminA, _) = await CreateRoleClient("ADMIN");
        var (adminB, _) = await CreateRoleClient("ADMIN");
        using (customerA) using (customerB) using (staff) using (adminA) using (adminB)
        {
            var (productId, sourceSkuId) = await CreateProductAndSku(customerAId, available: 10);
            var targetSkuId = Guid.NewGuid();
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
                VALUES(@target,@product,@code,'Last XL',200000,'ACTIVE');
                INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
                VALUES(gen_random_uuid(),@target,1,1,0,0,0);
                """, db))
            {
                insert.Parameters.AddWithValue("target", targetSkuId); insert.Parameters.AddWithValue("product", productId);
                insert.Parameters.AddWithValue("code", "LAST-" + targetSkuId.ToString("N")[..8]); await insert.ExecuteNonQueryAsync();
            }
            var orderA = await PrepareDeliveredCodOrderForSku(customerA, staff, addressA, productId, sourceSkuId);
            var orderB = await PrepareDeliveredCodOrderForSku(customerB, staff, addressB, productId, sourceSkuId);
            async Task<Guid> CreateRequest(HttpClient customer, (Guid OrderId, Guid ShipmentId, Guid OrderItemId, decimal TotalAmount) order)
            {
                using var response = await Send(customer, $"orders/{order.OrderId}/returns", new
                {
                    resolutionType = "EXCHANGE", reasonCode = "WRONG_SIZE", customerNote = "Tranh slot cuối",
                    items = new[] { new { orderItemId = order.OrderItemId, requestedQuantity = 1, targetSkuId } }
                });
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                return Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            }
            var returnA = await CreateRequest(customerA, orderA);
            var returnB = await CreateRequest(customerB, orderB);
            var approvals = await Task.WhenAll(
                Send(adminA, $"admin/returns/{returnA}/approve", new { decisionNote = "A" }),
                Send(adminB, $"admin/returns/{returnB}/approve", new { decisionNote = "B" }));
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, approvals.Select(r => r.StatusCode).OrderBy(x => x).ToArray());
            foreach (var response in approvals) response.Dispose();

            await using var checkDb = await fixture.OpenDatabaseAsync();
            await using var check = new NpgsqlCommand("""
                SELECT available_qty,reserved_qty,
                  (SELECT count(*) FROM store.exchange_stock_allocations WHERE product_sku_id=@target AND allocation_status='HELD')
                FROM store.inventory_balances WHERE product_sku_id=@target
                """, checkDb);
            check.Parameters.AddWithValue("target", targetSkuId);
            await using var reader = await check.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.Equal(0, reader.GetInt32(0)); Assert.Equal(1, reader.GetInt32(1)); Assert.Equal(1L, reader.GetInt64(2));
        }
    }

    [Fact]
    public async Task Expired_exchange_allocation_is_released_exactly_once()
    {
        var (customer, customerId, addressId) = await CreateCustomer("exchange_expiry");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            var (productId, sourceSkuId) = await CreateProductAndSku(customerId, available: 10);
            var targetSkuId = Guid.NewGuid();
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
                VALUES(@target,@product,@code,'Expiry XL',200000,'ACTIVE');
                INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
                VALUES(gen_random_uuid(),@target,1,1,0,0,0);
                """, db))
            {
                insert.Parameters.AddWithValue("target", targetSkuId); insert.Parameters.AddWithValue("product", productId);
                insert.Parameters.AddWithValue("code", "EXP-" + targetSkuId.ToString("N")[..8]); await insert.ExecuteNonQueryAsync();
            }
            var order = await PrepareDeliveredCodOrderForSku(customer, staff, addressId, productId, sourceSkuId);
            using var create = await Send(customer, $"orders/{order.OrderId}/returns", new
            {
                resolutionType = "EXCHANGE", reasonCode = "WRONG_SIZE", items = new[] { new { orderItemId = order.OrderItemId, requestedQuantity = 1, targetSkuId } }
            });
            var returnId = Guid.Parse((await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            Assert.Equal(HttpStatusCode.OK, (await Send(admin, $"admin/returns/{returnId}/approve", new { decisionNote = "Expiry" })).StatusCode);
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var expire = new NpgsqlCommand("UPDATE store.exchange_stock_allocations SET reserved_at=now()-interval '8 days', expires_at=now()-interval '1 second' WHERE return_request_id=@id", db))
            { expire.Parameters.AddWithValue("id", returnId); await expire.ExecuteNonQueryAsync(); }

            var worker = ActivatorUtilities.CreateInstance<ExchangeAllocationExpiryWorker>(fixture.Services);
            Assert.Equal(1, await worker.ReleaseExpiredAsync());
            Assert.Equal(0, await worker.ReleaseExpiredAsync());

            await using var checkDb = await fixture.OpenDatabaseAsync();
            await using var check = new NpgsqlCommand("""
                SELECT available_qty,reserved_qty,
                  (SELECT return_status FROM store.order_return_requests WHERE id=@return),
                  (SELECT count(*) FROM store.inventory_movements WHERE source_id=@return AND movement_type='EXCHANGE_RELEASE')
                FROM store.inventory_balances WHERE product_sku_id=@target
                """, checkDb);
            check.Parameters.AddWithValue("target", targetSkuId); check.Parameters.AddWithValue("return", returnId);
            await using var reader = await check.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.Equal(1, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal("EXCHANGE_EXPIRED", reader.GetString(2)); Assert.Equal(1L, reader.GetInt64(3));
        }
    }

    [Fact]
    public async Task Endpoints_Require_Correct_RBAC()
    {
        using var guest = fixture.CreateClient();
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, staffId) = await CreateRoleClient("STAFF");
        var (admin, adminId) = await CreateRoleClient("ADMIN");

        using (customer) using (staff) using (admin)
        {
            var (orderId, _, _, _, _, _) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId);

            // Eligibility: Guest -> 401, Staff/Admin -> 403, Customer -> 200
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/api/v1/orders/{orderId}/return-eligibility")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/v1/orders/{orderId}/return-eligibility")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/orders/{orderId}/return-eligibility")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/v1/orders/{orderId}/return-eligibility")).StatusCode);

            // Staff approved queue: Customer/Admin -> 403, Staff -> 200
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/staff/returns/approved")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/staff/returns/approved")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/staff/returns/approved")).StatusCode);

            // Admin returns list: Customer/Staff -> 403, Admin -> 200
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/returns")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/admin/returns")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/returns")).StatusCode);
        }
    }

    [Fact]
    public async Task Cross_Owner_Returns_404()
    {
        var (customerA, customerAId, addressAId) = await CreateCustomer("cust_a");
        var (customerB, _, _) = await CreateCustomer("cust_b");
        var (staff, _) = await CreateRoleClient("STAFF");

        using (customerA) using (customerB) using (staff)
        {
            var (orderId, _, _, _, orderItemId, _) = await PrepareDeliveredCodOrder(customerA, staff, customerAId, addressAId);

            // Customer B queries eligibility for Order of Customer A -> 404
            var respEligibility = await customerB.GetAsync($"/api/v1/orders/{orderId}/return-eligibility");
            Assert.Equal(HttpStatusCode.NotFound, respEligibility.StatusCode);

            // Customer B tries to create return for Order of Customer A -> 404
            var respCreate = await Send(customerB, $"orders/{orderId}/returns", new
            {
                reasonCode = "DEFECTIVE",
                customerNote = "Lỗi sản phẩm",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });
            Assert.Equal(HttpStatusCode.NotFound, respCreate.StatusCode);
        }
    }

    [Fact]
    public async Task Return_Window_Expired_Cannot_Create_Return()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");

        using (customer) using (staff)
        {
            var (orderId, shipmentId, _, _, orderItemId, _) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId);

            // Manually age the delivery to 8 days ago
            await using var db = await fixture.OpenDatabaseAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE store.shipments SET delivered_at = now() - interval '8 days' WHERE id=@shipmentId", db);
            cmd.Parameters.AddWithValue("shipmentId", shipmentId);
            await cmd.ExecuteNonQueryAsync();

            // Eligibility check returns false with message
            var eligResp = await customer.GetAsync($"/api/v1/orders/{orderId}/return-eligibility");
            Assert.Equal(HttpStatusCode.OK, eligResp.StatusCode);
            var eligJson = await eligResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(eligJson.GetProperty("isEligible").GetBoolean());

            // Creating returns 400 RETURN_WINDOW_EXPIRED
            var createResp = await Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "WRONG_SIZE",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });
            Assert.Equal(HttpStatusCode.BadRequest, createResp.StatusCode);
        }
    }

    [Fact]
    public async Task Customer_Can_Create_And_Cancel_Return_Request()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");

        using (customer) using (staff)
        {
            var (orderId, _, _, _, orderItemId, orderTotalAmount) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId);

            // Create return request
            var createResp = await Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "WRONG_SIZE",
                customerNote = "Đổi cỡ khác",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var detail = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            var returnId = Guid.Parse(detail.GetProperty("id").GetString()!);
            Assert.Equal("REQUESTED", detail.GetProperty("returnStatus").GetString());
            Assert.Equal(200_000m, detail.GetProperty("refundAmount").GetDecimal());
            Assert.Equal(0m, detail.GetProperty("shippingFee").GetDecimal());
            Assert.Equal(orderTotalAmount, detail.GetProperty("orderTotalAmount").GetDecimal());

            // Check list in account returns
            var listResp = await customer.GetAsync("/api/v1/account/returns");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(listJson.GetProperty("totalCount").GetInt32() >= 1);

            // Detail endpoint
            var getDetailResp = await customer.GetAsync($"/api/v1/account/returns/{returnId}");
            Assert.Equal(HttpStatusCode.OK, getDetailResp.StatusCode);
            var customerDetail = await getDetailResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0m, customerDetail.GetProperty("shippingFee").GetDecimal());

            // Cancel
            var cancelResp = await Send(customer, $"account/returns/{returnId}/cancel");
            Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
            var cancelledDetail = await cancelResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CANCELLED", cancelledDetail.GetProperty("returnStatus").GetString());

            // Idempotent cancel
            var cancelAgain = await Send(customer, $"account/returns/{returnId}/cancel");
            Assert.Equal(HttpStatusCode.OK, cancelAgain.StatusCode);
        }
    }

    [Fact]
    public async Task Full_Return_End_To_End_Lifecycle_Restocks_And_Refunds()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");

        using (customer) using (staff) using (admin)
        {
            var (orderId, _, _, skuId, orderItemId, totalAmount) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId, quantity: 2);

            // Check initial available qty after delivery (should be 8 out of 10)
            await using var db = await fixture.OpenDatabaseAsync();
            await using var checkQty = new NpgsqlCommand("SELECT physical_qty, available_qty FROM store.inventory_balances WHERE product_sku_id=@sku", db);
            checkQty.Parameters.AddWithValue("sku", skuId);
            await using (var reader = await checkQty.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(8, reader.GetInt32(0));
                Assert.Equal(8, reader.GetInt32(1));
            }

            // 1. Customer creates return request for 1 item
            var createResp = await Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "DEFECTIVE",
                customerNote = "Sản phẩm bị lỗi chỉ may",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var returnJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            var returnId = Guid.Parse(returnJson.GetProperty("id").GetString()!);

            // 2. Admin approves
            var approveResp = await Send(admin, $"admin/returns/{returnId}/approve", new
            {
                decisionNote = "Đồng ý nhận lại 1 sản phẩm bị lỗi"
            });
            Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
            var approvedJson = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("APPROVED", approvedJson.GetProperty("returnStatus").GetString());

            // 3. Staff sees return in approved queue
            var queueResp = await staff.GetAsync("/api/v1/staff/returns/approved");
            Assert.Equal(HttpStatusCode.OK, queueResp.StatusCode);
            var queueJson = await queueResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(queueJson.GetProperty("items").EnumerateArray(),
                x => Guid.Parse(x.GetProperty("id").GetString()!) == returnId);

            // 4. Staff receives goods into quarantine; they are not sellable yet.
            var receiveResp = await Send(staff, $"staff/returns/{returnId}/receive", new
            {
                receivedEvidence = "Biên bản nhận hàng trả BH-2026-001"
            });
            Assert.Equal(HttpStatusCode.OK, receiveResp.StatusCode);
            var receivedJson = await receiveResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("RECEIVED", receivedJson.GetProperty("returnStatus").GetString());
            var returnLineId = receivedJson.GetProperty("lines")[0].GetProperty("id").GetGuid();

            // Physical increases, but available remains unchanged until inspection.
            await using var checkQtyAfter = new NpgsqlCommand("SELECT physical_qty, available_qty, quarantined_qty FROM store.inventory_balances WHERE product_sku_id=@sku", db);
            checkQtyAfter.Parameters.AddWithValue("sku", skuId);
            await using (var reader = await checkQtyAfter.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(9, reader.GetInt32(0));
                Assert.Equal(8, reader.GetInt32(1));
                Assert.Equal(1, reader.GetInt32(2));
            }

            // Verify exactly 1 RETURN_IN inventory movement
            await using var checkMovement = new NpgsqlCommand(
                "SELECT count(*) FROM store.inventory_movements WHERE source_type='ORDER_RETURN' AND source_id=@returnId AND movement_type='RETURN_IN' AND qty_delta=1", db);
            checkMovement.Parameters.AddWithValue("returnId", returnId);
            var movementCount = (long)(await checkMovement.ExecuteScalarAsync())!;
            Assert.Equal(1L, movementCount);

            // Verify payment status is REFUND_REQUIRED
            await using var checkPayment = new NpgsqlCommand("SELECT payment_status FROM store.payments WHERE order_id=@orderId", db);
            checkPayment.Parameters.AddWithValue("orderId", orderId);
            Assert.Equal("REFUND_REQUIRED", (string)(await checkPayment.ExecuteScalarAsync())!);

            // 5. Admin cannot refund before Staff completes quality inspection.
            var prematureRefund = await Send(admin, $"admin/returns/{returnId}/refund", new { evidenceReference = "PREMATURE-" + Guid.NewGuid().ToString("N")[..8] });
            Assert.Equal(HttpStatusCode.BadRequest, prematureRefund.StatusCode);

            var inspectResp = await Send(staff, $"staff/returns/{returnId}/inspect", new
            {
                inspectionNote = "Hàng nguyên vẹn, đủ điều kiện bán lại",
                lines = new[] { new { returnLineId, restockQty = 1, quarantineQty = 0, damagedQty = 0, scrappedQty = 0 } }
            });
            Assert.Equal(HttpStatusCode.OK, inspectResp.StatusCode);
            Assert.Equal("INSPECTED", (await inspectResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnStatus").GetString());

            await using var checkAfterInspection = new NpgsqlCommand("SELECT physical_qty, available_qty, quarantined_qty, damaged_qty FROM store.inventory_balances WHERE product_sku_id=@sku", db);
            checkAfterInspection.Parameters.AddWithValue("sku", skuId);
            await using (var reader = await checkAfterInspection.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(9, reader.GetInt32(0));
                Assert.Equal(9, reader.GetInt32(1));
                Assert.Equal(0, reader.GetInt32(2));
                Assert.Equal(0, reader.GetInt32(3));
            }

            // 6. Admin records manual refund proof
            var refundEvidence = "REF-PROOF-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var refundResp = await Send(admin, $"admin/returns/{returnId}/refund", new
            {
                evidenceReference = refundEvidence
            });
            Assert.Equal(HttpStatusCode.OK, refundResp.StatusCode);
            var refundJson = await refundResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("REFUNDED", refundJson.GetProperty("returnStatus").GetString());

            // Partial return of 1 out of 2 units -> payment should be PARTIALLY_REFUNDED
            await using var checkPaymentFinal = new NpgsqlCommand("SELECT payment_status FROM store.payments WHERE order_id=@orderId", db);
            checkPaymentFinal.Parameters.AddWithValue("orderId", orderId);
            Assert.Equal("PARTIALLY_REFUNDED", (string)(await checkPaymentFinal.ExecuteScalarAsync())!);

            // 7. Test idempotent retry of refund
            var refundRetry = await Send(admin, $"admin/returns/{returnId}/refund", new
            {
                evidenceReference = refundEvidence
            });
            Assert.Equal(HttpStatusCode.OK, refundRetry.StatusCode);

            // Customer-facing history exposes the manual refund proof reference.
            using var customerHistory = await customer.GetAsync("/api/v1/account/returns");
            Assert.Equal(HttpStatusCode.OK, customerHistory.StatusCode);
            var customerHistoryJson = await customerHistory.Content.ReadFromJsonAsync<JsonElement>();
            var refundedSummary = Assert.Single(
                customerHistoryJson.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == returnId);
            Assert.Equal(refundEvidence, refundedSummary.GetProperty("evidenceReference").GetString());

            // 8. Test audit logs recorded properly
            await using var checkAudit = new NpgsqlCommand(
                "SELECT count(*) FROM store.audit_logs WHERE entity_id=@returnId AND action IN ('RETURN_REQUESTED','RETURN_APPROVED','RETURN_RECEIVED','RETURN_INSPECTED','RETURN_REFUNDED')", db);
            checkAudit.Parameters.AddWithValue("returnId", returnId);
            var auditCount = (long)(await checkAudit.ExecuteScalarAsync())!;
            Assert.Equal(5L, auditCount);
        }
    }

    [Fact]
    public async Task Inspection_Classifies_All_Dispositions_And_Is_Idempotent()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            var (orderId, _, _, skuId, orderItemId, _) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId, quantity: 4);
            var created = await Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "DEFECTIVE",
                customerNote = "Cần kiểm định riêng từng sản phẩm",
                items = new[] { new { orderItemId, requestedQuantity = 4 } }
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
            var returnId = createdJson.GetProperty("id").GetGuid();
            var returnLineId = createdJson.GetProperty("lines")[0].GetProperty("id").GetGuid();

            Assert.Equal(HttpStatusCode.OK, (await Send(admin, $"admin/returns/{returnId}/approve", new { decisionNote = "Duyệt kiểm định" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/returns/{returnId}/receive", new { receivedEvidence = "BB-T41-001" })).StatusCode);

            var payload = new
            {
                inspectionNote = "Một bán lại, một cách ly, một hư hỏng, một loại bỏ",
                lines = new[] { new { returnLineId, restockQty = 1, quarantineQty = 1, damagedQty = 1, scrappedQty = 1 } }
            };
            var results = await Task.WhenAll(
                Send(staff, $"staff/returns/{returnId}/inspect", payload),
                Send(staff, $"staff/returns/{returnId}/inspect", payload));
            Assert.All(results, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

            await using var db = await fixture.OpenDatabaseAsync();
            await using var balanceCommand = new NpgsqlCommand(
                "SELECT physical_qty,available_qty,reserved_qty,allocated_qty,quarantined_qty,damaged_qty FROM store.inventory_balances WHERE product_sku_id=@sku", db);
            balanceCommand.Parameters.AddWithValue("sku", skuId);
            await using (var reader = await balanceCommand.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(9, reader.GetInt32(0));
                Assert.Equal(7, reader.GetInt32(1));
                Assert.Equal(0, reader.GetInt32(2));
                Assert.Equal(0, reader.GetInt32(3));
                Assert.Equal(1, reader.GetInt32(4));
                Assert.Equal(1, reader.GetInt32(5));
            }

            await using var movementCommand = new NpgsqlCommand(
                "SELECT count(*) FROM store.inventory_movements WHERE source_type='RETURN_INSPECTION' AND source_id=@returnId", db);
            movementCommand.Parameters.AddWithValue("returnId", returnId);
            Assert.Equal(3L, (long)(await movementCommand.ExecuteScalarAsync())!);

            var conflict = await Send(staff, $"staff/returns/{returnId}/inspect", new
            {
                inspectionNote = payload.inspectionNote,
                lines = new[] { new { returnLineId, restockQty = 4, quarantineQty = 0, damagedQty = 0, scrappedQty = 0 } }
            });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
    }

    [Fact]
    public async Task Full_Return_Of_Product_Hides_Active_Review()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");

        using (customer) using (staff) using (admin)
        {
            // Buy exactly 1 unit
            var (orderId, _, productId, skuId, orderItemId, _) = await PrepareDeliveredCodOrder(
                customer, staff, customerId, addressId, quantity: 1, unitPrice: 300_000m);

            // Customer posts a review for this product
            var reviewResp = await Send(customer, $"products/{productId}/reviews", new
            {
                rating = 5,
                content = "Sản phẩm dùng rất tốt, hài lòng với chất lượng!"
            });
            Assert.Equal(HttpStatusCode.Created, reviewResp.StatusCode);
            var reviewJson = await reviewResp.Content.ReadFromJsonAsync<JsonElement>();
            var reviewId = Guid.Parse(reviewJson.GetProperty("id").GetString()!);
            Assert.Equal("PUBLISHED", reviewJson.GetProperty("reviewStatus").GetString());

            // Customer creates full return for this order
            var createResp = await Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "DEFECTIVE",
                customerNote = "Sản phẩm bị lỗi hỏng",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var returnId = Guid.Parse((await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

            // Admin approves -> Staff receives -> Staff inspects -> Admin refunds
            Assert.Equal(HttpStatusCode.OK, (await Send(admin, $"admin/returns/{returnId}/approve", new { decisionNote = "Duyệt" })).StatusCode);
            var receive = await Send(staff, $"staff/returns/{returnId}/receive", new { receivedEvidence = "Đã nhận" });
            Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
            var received = await receive.Content.ReadFromJsonAsync<JsonElement>();
            var returnLineId = received.GetProperty("lines")[0].GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await Send(staff, $"staff/returns/{returnId}/inspect", new
            {
                inspectionNote = "Sản phẩm lỗi, giữ khu hư hỏng",
                lines = new[] { new { returnLineId, restockQty = 0, quarantineQty = 0, damagedQty = 1, scrappedQty = 0 } }
            })).StatusCode);

            var refundResp = await Send(admin, $"admin/returns/{returnId}/refund", new
            {
                evidenceReference = "REFUND-REV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()
            });
            Assert.Equal(HttpStatusCode.OK, refundResp.StatusCode);

            // Verify review status transitioned to HIDDEN with reason RETURN_REFUNDED
            await using var db = await fixture.OpenDatabaseAsync();
            await using var checkReview = new NpgsqlCommand(
                "SELECT review_status, moderation_reason FROM store.product_reviews WHERE id=@reviewId", db);
            checkReview.Parameters.AddWithValue("reviewId", reviewId);
            await using (var reader = await checkReview.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("HIDDEN", reader.GetString(0));
                Assert.Equal("RETURN_REFUNDED", reader.GetString(1));
            }
        }
    }

    [Fact]
    public async Task Concurrent_Full_Refund_And_Review_Creation_Never_Leaves_A_Public_Review()
    {
        var (customer, customerId, addressId) = await CreateCustomer("reviewrefundrace");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");

        using (customer) using (staff) using (admin)
        {
            var (orderId, _, productId, _, orderItemId, _) = await PrepareDeliveredCodOrder(
                customer, staff, customerId, addressId, quantity: 1, unitPrice: 300_000m);

            using var createReturn = await Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "DEFECTIVE",
                customerNote = "Hoàn toàn bộ để kiểm tra race với tạo review",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });
            Assert.Equal(HttpStatusCode.Created, createReturn.StatusCode);
            var returnId = (await createReturn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            using var approve = await Send(admin, $"admin/returns/{returnId}/approve", new { decisionNote = "Duyệt race test" });
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            using var receive = await Send(staff, $"staff/returns/{returnId}/receive", new { receivedEvidence = "RACE-RECEIVED" });
            Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
            var received = await receive.Content.ReadFromJsonAsync<JsonElement>();
            var returnLineId = received.GetProperty("lines")[0].GetProperty("id").GetGuid();
            using var inspect = await Send(staff, $"staff/returns/{returnId}/inspect", new
            {
                inspectionNote = "Khóa race review và refund",
                lines = new[] { new { returnLineId, restockQty = 0, quarantineQty = 0, damagedQty = 1, scrappedQty = 0 } }
            });
            Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);

            await using var blocker = await fixture.OpenDatabaseAsync();
            await using var blockerTx = await blocker.BeginTransactionAsync();
            await using (var lockOrder = new NpgsqlCommand(
                "SELECT id FROM store.sales_orders WHERE id=@orderId FOR UPDATE", blocker, blockerTx))
            {
                lockOrder.Parameters.AddWithValue("orderId", orderId);
                Assert.Equal(orderId, (Guid)(await lockOrder.ExecuteScalarAsync())!);
            }

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reviewTask = Task.Run(async () =>
            {
                await start.Task;
                using var response = await Send(customer, $"products/{productId}/reviews", new
                {
                    rating = 5,
                    content = "Đánh giá được gửi đồng thời với hoàn tiền toàn phần."
                });
                var body = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, Body: body);
            });
            var refundTask = Task.Run(async () =>
            {
                await start.Task;
                using var response = await Send(admin, $"admin/returns/{returnId}/refund", new
                {
                    evidenceReference = "REFUND-RACE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()
                });
                return response.StatusCode;
            });

            start.SetResult();

            var waitDeadline = DateTime.UtcNow.AddSeconds(10);
            var lockWaiterCount = 0L;
            while (DateTime.UtcNow < waitDeadline)
            {
                await using var observer = await fixture.OpenDatabaseAsync();
                await using var waiting = new NpgsqlCommand("""
                    SELECT count(*)
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE '%store.sales_orders%'
                    """, observer);
                lockWaiterCount = (long)(await waiting.ExecuteScalarAsync())!;
                if (lockWaiterCount >= 2) break;
                await Task.Delay(50);
            }
            Assert.True(lockWaiterCount >= 2, $"Expected both requests to wait on the Order row lock, observed {lockWaiterCount}.");

            await blockerTx.CommitAsync();
            var reviewOutcome = await reviewTask.WaitAsync(TimeSpan.FromSeconds(30));
            var refundStatus = await refundTask.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(HttpStatusCode.OK, refundStatus);
            Assert.Contains(reviewOutcome.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            if (reviewOutcome.StatusCode == HttpStatusCode.Conflict)
            {
                using var problem = JsonDocument.Parse(reviewOutcome.Body);
                Assert.Equal("REVIEW_NOT_ELIGIBLE", problem.RootElement.GetProperty("code").GetString());
            }

            await using var verify = await fixture.OpenDatabaseAsync();
            await using var invariant = new NpgsqlCommand("""
                SELECT
                    (SELECT return_status FROM store.order_return_requests WHERE id=@returnId),
                    (SELECT count(*) FROM store.product_reviews
                     WHERE customer_id=@customerId AND product_id=@productId
                       AND review_status='PUBLISHED' AND deleted_at IS NULL)
                """, verify);
            invariant.Parameters.AddWithValue("returnId", returnId);
            invariant.Parameters.AddWithValue("customerId", customerId);
            invariant.Parameters.AddWithValue("productId", productId);
            await using var invariantReader = await invariant.ExecuteReaderAsync();
            Assert.True(await invariantReader.ReadAsync());
            Assert.Equal("REFUNDED", invariantReader.GetString(0));
            Assert.Equal(0L, invariantReader.GetInt64(1));
        }
    }

    [Fact]
    public async Task Concurrent_Create_Return_Requests_Allows_Only_One()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");

        using (customer) using (staff)
        {
            var (orderId, _, _, _, orderItemId, _) = await PrepareDeliveredCodOrder(customer, staff, customerId, addressId);

            var task1 = Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "WRONG_SIZE",
                customerNote = "Concurrent 1",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });

            var task2 = Send(customer, $"orders/{orderId}/returns", new
            {
                reasonCode = "WRONG_SIZE",
                customerNote = "Concurrent 2",
                items = new[] { new { orderItemId, requestedQuantity = 1 } }
            });

            var responses = await Task.WhenAll(task1, task2);

            var statusCodes = responses.Select(r => r.StatusCode).ToList();
            Assert.Contains(HttpStatusCode.Created, statusCodes);
            Assert.Contains(HttpStatusCode.Conflict, statusCodes);
        }
    }
}

