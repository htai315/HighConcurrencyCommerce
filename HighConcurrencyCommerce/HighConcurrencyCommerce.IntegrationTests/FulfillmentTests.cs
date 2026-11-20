using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class FulfillmentTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure fulfillment pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, string? key = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AddressId)> CreateCustomer()
    {
        var email = $"fulfillment_{Guid.NewGuid():N}@example.invalid";
        var client = fixture.CreateClient();
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Fulfillment Customer" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await fixture.VerifyUserAsync(email);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var addressResponse = await Send(client, "account/addresses", new
        {
            recipientName = "Người nhận", phone = "0901234567", addressLine = "25 Đường Giao Hàng", province = "Đà Nẵng", isDefault = true
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
        var email = $"fulfillment_{roleKey}_{Guid.NewGuid():N}@example.invalid";
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
        insert.Parameters.AddWithValue("name", roleKey + " Fulfillment");
        await insert.ExecuteNonQueryAsync();
        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, id);
    }

    private async Task<Guid> CreateSku(Guid actorId, int available = 5, decimal price = 350000)
    {
        var product = Guid.NewGuid();
        var sku = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at) VALUES(@product,@title,'PUBLISHED',@actor,@actor,now()); " +
            "INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) VALUES(@sku,@product,@code,'4U G5',@price,'ACTIVE'); " +
            "INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision) VALUES(gen_random_uuid(),@sku,@available,@available,0,0,0);", db);
        command.Parameters.AddWithValue("product", product);
        command.Parameters.AddWithValue("sku", sku);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("title", "Vợt fulfillment " + product.ToString("N")[..8]);
        command.Parameters.AddWithValue("code", "FUL-" + sku.ToString("N")[..10].ToUpperInvariant());
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        return sku;
    }

    private async Task<(Guid OrderId, Guid ShipmentId, Guid SkuId)> PreparePackedShipment(
        HttpClient customer, HttpClient staff, Guid customerId, Guid addressId, int quantity = 2)
    {
        var sku = await CreateSku(customerId);
        using var checkout = await Send(customer, "checkouts", new
        {
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee, paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku, quantity, flashSaleOfferId = (Guid?)null } }
        }, "fulfillment-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var orderId = Guid.Parse((await checkout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetString()!);
        using var confirm = await Send(staff, $"staff/orders/{orderId}/confirm-cod");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        using var start = await Send(staff, $"staff/orders/{orderId}/start-fulfillment");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var shipmentId = Guid.Parse((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shipmentId").GetString()!);
        using var pack = await Send(staff, $"staff/shipments/{shipmentId}/pack");
        Assert.Equal(HttpStatusCode.OK, pack.StatusCode);
        return (orderId, shipmentId, sku);
    }

    private async Task<(Guid ConsignmentId, long Revision)> PrepareCarrierConsignment(
        HttpClient admin, HttpClient staff, Guid shipmentId)
    {
        var code = "FC" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var createdCarrier = await Send(admin, "admin/carriers", new
        {
            code, name = "Fulfillment Carrier",
            trackingUrlTemplate = "https://tracking.example.com/{trackingNumber}"
        });
        Assert.Equal(HttpStatusCode.Created, createdCarrier.StatusCode);
        var carrierId = (await createdCarrier.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var activated = await Send(admin, $"admin/carriers/{carrierId}/activate", new { expectedRevision = 1L });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        using var created = await Send(staff, $"staff/carrier-consignments/sales-shipments/{shipmentId}", new
        {
            carrierId, trackingNumber = "FUL-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant()
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var consignmentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/staff/carrier-consignments/{consignmentId}/label");
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7\nfulfillment label"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(file, "file", "label.pdf");
        multipart.Add(new StringContent("1"), "expectedRevision");
        request.Content = multipart;
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(staff));
        using var label = await staff.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, label.StatusCode);
        var revision = (await label.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64();
        return (consignmentId, revision);
    }

    [Fact]
    public async Task Fulfillment_endpoints_are_staff_only()
    {
        using var guest = fixture.CreateClient();
        var (customer, _, _) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/staff/shipments")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/staff/shipments")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/staff/shipments")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/staff/shipments")).StatusCode);
        }
    }

    [Fact]
    public async Task Start_pack_and_handover_consumes_allocation_and_stocks_out_once()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        using (customer) using (staff)
        {
            var (orderId, shipmentId, sku) = await PreparePackedShipment(customer, staff, customerId, addressId);
            using var handover = await Send(staff, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
            var body = await handover.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("SHIPPING", body.GetProperty("shipmentStatus").GetString());
            Assert.Equal("FULFILLING", body.GetProperty("orderStatus").GetString());
            Assert.Equal(0m, body.GetProperty("shippingFee").GetDecimal());
            Assert.Equal("TEST_FREE", body.GetProperty("shippingMethodCode").GetString());
            Assert.Equal("Giao hàng test miễn phí", body.GetProperty("shippingMethodName").GetString());
            Assert.Equal(1, body.GetProperty("shippingEstimatedMinDays").GetInt16());
            Assert.Equal(3, body.GetProperty("shippingEstimatedMaxDays").GetInt16());

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku; " +
                "SELECT reservation_status FROM store.stock_reservations WHERE order_id=@order; " +
                "SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND movement_type='STOCK_OUT' AND qty_delta=-2; " +
                "SELECT count(*) FROM store.delivery_attempts WHERE shipment_id=@shipment AND attempt_no=1 AND result='IN_PROGRESS';", db);
            query.Parameters.AddWithValue("sku", sku); query.Parameters.AddWithValue("order", orderId); query.Parameters.AddWithValue("shipment", shipmentId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3, reader.GetInt32(0)); Assert.Equal(3, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal("CONSUMED", reader.GetString(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
        }
    }

    [Fact]
    public async Task Legacy_and_carrier_handover_share_one_atomic_sales_transition()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staffA, _) = await CreateRoleClient("STAFF");
        var (staffB, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staffA) using (staffB) using (admin)
        {
            var (orderId, shipmentId, skuId) = await PreparePackedShipment(customer, staffA, customerId, addressId);
            var (consignmentId, revision) = await PrepareCarrierConsignment(admin, staffA, shipmentId);

            var handovers = await Task.WhenAll(
                Send(staffA, $"staff/shipments/{shipmentId}/handover"),
                Send(staffB, $"staff/carrier-consignments/{consignmentId}/handover",
                    new { expectedRevision = revision }));
            Assert.All(handovers, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            foreach (var response in handovers) response.Dispose();

            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND product_sku_id=@sku AND movement_type='STOCK_OUT'),
                  (SELECT count(*) FROM store.delivery_attempts WHERE shipment_id=@shipment AND attempt_no=1),
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@consignment AND event_type='HANDED_OVER'),
                  (SELECT consignment_status FROM store.carrier_consignments WHERE id=@consignment)
                """, db))
            {
                check.Parameters.AddWithValue("order", orderId);
                check.Parameters.AddWithValue("sku", skuId);
                check.Parameters.AddWithValue("shipment", shipmentId);
                check.Parameters.AddWithValue("consignment", consignmentId);
                await using var reader = await check.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal("HANDED_OVER", reader.GetString(3));
            }

            using var delivered = await Send(staffA, $"staff/shipments/{shipmentId}/delivery-result", new
            {
                result = "DELIVERED", codCollectedAmount = 700000m,
                collectionReference = "CARRIER-COD-" + Guid.NewGuid().ToString("N")[..8]
            });
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            await using var finalDb = await fixture.OpenDatabaseAsync();
            await using var finalCheck = new NpgsqlCommand("""
                SELECT consignment_status,delivered_at IS NOT NULL,
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@id AND event_type='DELIVERED')
                FROM store.carrier_consignments WHERE id=@id
                """, finalDb);
            finalCheck.Parameters.AddWithValue("id", consignmentId);
            await using var finalReader = await finalCheck.ExecuteReaderAsync();
            Assert.True(await finalReader.ReadAsync());
            Assert.Equal("DELIVERED", finalReader.GetString(0));
            Assert.True(finalReader.GetBoolean(1));
            Assert.Equal(1L, finalReader.GetInt64(2));
        }
    }

    [Fact]
    public async Task Carrier_timeline_follows_failed_retry_and_return_stock_commands()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            var (_, shipmentId, _) = await PreparePackedShipment(customer, staff, customerId, addressId);
            var (consignmentId, revision) = await PrepareCarrierConsignment(admin, staff, shipmentId);
            using var handover = await Send(staff, $"staff/carrier-consignments/{consignmentId}/handover",
                new { expectedRevision = revision });
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);

            using var failed = await Send(staff, $"staff/shipments/{shipmentId}/delivery-result",
                new { result = "FAILED", failureReason = "Khách vắng mặt" });
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            using var retry = await Send(staff, $"staff/shipments/{shipmentId}/retry");
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            using var failedAgain = await Send(staff, $"staff/shipments/{shipmentId}/delivery-result",
                new { result = "FAILED", failureReason = "Khách tiếp tục vắng mặt" });
            Assert.Equal(HttpStatusCode.OK, failedAgain.StatusCode);
            using var returned = await Send(staff, $"staff/shipments/{shipmentId}/return-stock",
                new { receivedEvidence = "Phiếu nhận lại carrier T49" });
            Assert.Equal(HttpStatusCode.OK, returned.StatusCode);

            await using var db = await fixture.OpenDatabaseAsync();
            await using var check = new NpgsqlCommand("""
                SELECT consignment_status,
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@id AND event_type='DELIVERY_FAILED'),
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@id AND event_type='REDELIVERY_STARTED'),
                  (SELECT count(*) FROM store.carrier_tracking_events WHERE consignment_id=@id AND event_type='RETURNED')
                FROM store.carrier_consignments WHERE id=@id
                """, db);
            check.Parameters.AddWithValue("id", consignmentId);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("RETURNED", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }
    }

    [Fact]
    public async Task Delivered_with_exact_cod_collection_completes_order_atomically()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, staffId) = await CreateRoleClient("STAFF");
        using (customer) using (staff)
        {
            var (orderId, shipmentId, _) = await PreparePackedShipment(customer, staff, customerId, addressId);
            using var handover = await Send(staff, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
            using var delivered = await Send(staff, $"staff/shipments/{shipmentId}/delivery-result",
                new { result = "DELIVERED", codCollectedAmount = 700000m, collectionReference = "COD-RECEIPT-001" });
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            var body = await delivered.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("COMPLETED", body.GetProperty("orderStatus").GetString());
            Assert.Equal("DELIVERED", body.GetProperty("shipmentStatus").GetString());
            Assert.Equal("COD_COLLECTED", body.GetProperty("paymentStatus").GetString());

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM store.payment_transactions t JOIN store.payments p ON p.id=t.payment_id " +
                "WHERE p.order_id=@order AND t.provider='COD' AND t.amount=700000 AND t.provider_transaction_id='COD-RECEIPT-001' AND t.transaction_status='VERIFIED'; " +
                "SELECT count(*) FROM store.audit_logs WHERE entity_id=@order AND action='COD_DELIVERY_COMPLETED' AND actor_id=@staff;", db);
            query.Parameters.AddWithValue("order", orderId); query.Parameters.AddWithValue("staff", staffId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
        }
    }

    [Fact]
    public async Task Cod_amount_mismatch_rolls_back_delivery_state()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        using (customer) using (staff)
        {
            var (orderId, shipmentId, _) = await PreparePackedShipment(customer, staff, customerId, addressId);
            using var handover = await Send(staff, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
            using var mismatch = await Send(staff, $"staff/shipments/{shipmentId}/delivery-result",
                new { result = "DELIVERED", codCollectedAmount = 699999m, collectionReference = "COD-WRONG-001" });
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            Assert.Equal("COD_AMOUNT_MISMATCH", (await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT o.order_status,s.shipment_status,p.payment_status,(SELECT count(*) FROM store.payment_transactions WHERE payment_id=p.id) " +
                "FROM store.sales_orders o JOIN store.shipments s ON s.order_id=o.id JOIN store.payments p ON p.order_id=o.id WHERE o.id=@order", db);
            query.Parameters.AddWithValue("order", orderId);
            await using var reader = await query.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal("FULFILLING", reader.GetString(0)); Assert.Equal("SHIPPING", reader.GetString(1)); Assert.Equal("COD_PENDING", reader.GetString(2)); Assert.Equal(0L, reader.GetInt64(3));
        }
    }

    [Fact]
    public async Task Concurrent_double_delivery_collects_cod_exactly_once()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staffA, _) = await CreateRoleClient("STAFF");
        var (staffB, _) = await CreateRoleClient("STAFF");
        using (customer) using (staffA) using (staffB)
        {
            var (orderId, shipmentId, _) = await PreparePackedShipment(customer, staffA, customerId, addressId);
            using var handover = await Send(staffA, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
            var payload = new { result = "DELIVERED", codCollectedAmount = 700000m, collectionReference = "COD-RACE-001" };
            var responses = await Task.WhenAll(
                Send(staffA, $"staff/shipments/{shipmentId}/delivery-result", payload),
                Send(staffB, $"staff/shipments/{shipmentId}/delivery-result", payload));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses) response.Dispose();
            await using var db = await fixture.OpenDatabaseAsync();
            await using var count = new NpgsqlCommand(
                "SELECT " +
                "(SELECT count(*) FROM store.payment_transactions t JOIN store.payments p ON p.id=t.payment_id WHERE p.order_id=@order)," +
                "(SELECT count(*) FROM store.in_app_notifications WHERE recipient_id=@customer AND event_key=@event)", db);
            count.Parameters.AddWithValue("order", orderId);
            count.Parameters.AddWithValue("customer", customerId);
            count.Parameters.AddWithValue("event", $"order:{orderId:N}:delivered");
            await using var reader = await count.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task Failed_delivery_allows_two_redeliveries_then_rejects_the_third()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staff, _) = await CreateRoleClient("STAFF");
        using (customer) using (staff)
        {
            var (orderId, shipmentId, _) = await PreparePackedShipment(customer, staff, customerId, addressId);
            using var handover = await Send(staff, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);

            for (var attemptNo = 1; attemptNo <= 3; attemptNo++)
            {
                using var failed = await Send(staff, $"staff/shipments/{shipmentId}/delivery-result",
                    new { result = "FAILED", failureReason = $"Khách vắng mặt lần {attemptNo}" });
                Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
                var failedBody = await failed.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("DELIVERY_FAILED", failedBody.GetProperty("shipmentStatus").GetString());
                Assert.Equal(attemptNo - 1, failedBody.GetProperty("redeliveryCount").GetInt32());

                if (attemptNo < 3)
                {
                    using var retry = await Send(staff, $"staff/shipments/{shipmentId}/retry");
                    Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
                    var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal("SHIPPING", retryBody.GetProperty("shipmentStatus").GetString());
                    Assert.Equal(attemptNo, retryBody.GetProperty("redeliveryCount").GetInt32());
                }
            }

            using var exhausted = await Send(staff, $"staff/shipments/{shipmentId}/retry");
            Assert.Equal(HttpStatusCode.Conflict, exhausted.StatusCode);
            Assert.Equal("REDELIVERY_LIMIT_REACHED",
                (await exhausted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM store.delivery_attempts WHERE shipment_id=@shipment AND result='FAILED'; " +
                "SELECT count(*) FROM store.payment_transactions t JOIN store.payments p ON p.id=t.payment_id WHERE p.order_id=@order;", db);
            query.Parameters.AddWithValue("shipment", shipmentId); query.Parameters.AddWithValue("order", orderId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(3L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(0L, reader.GetInt64(0));
        }
    }

    [Fact]
    public async Task Concurrent_return_stock_restores_inventory_exactly_once()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staffA, _) = await CreateRoleClient("STAFF");
        var (staffB, _) = await CreateRoleClient("STAFF");
        using (customer) using (staffA) using (staffB)
        {
            var (orderId, shipmentId, skuId) = await PreparePackedShipment(customer, staffA, customerId, addressId);
            using var handover = await Send(staffA, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
            using var failed = await Send(staffA, $"staff/shipments/{shipmentId}/delivery-result",
                new { result = "FAILED", failureReason = "Khách từ chối nhận hàng" });
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

            var responses = await Task.WhenAll(
                Send(staffA, $"staff/shipments/{shipmentId}/return-stock", new { receivedEvidence = "Biên bản nhận lại BB-001" }),
                Send(staffB, $"staff/shipments/{shipmentId}/return-stock", new { receivedEvidence = "Biên bản nhận lại BB-001" }));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku; " +
                "SELECT o.order_status,o.closed_reason,p.payment_status,s.shipment_status,s.return_stock_completed,r.reservation_status " +
                "FROM store.sales_orders o JOIN store.payments p ON p.order_id=o.id JOIN store.shipments s ON s.order_id=o.id " +
                "JOIN store.stock_reservations r ON r.order_id=o.id WHERE o.id=@order; " +
                "SELECT count(*) FROM store.inventory_movements WHERE source_id=@order AND product_sku_id=@sku AND movement_type='RETURN_IN' AND qty_delta=2;", db);
            query.Parameters.AddWithValue("sku", skuId); query.Parameters.AddWithValue("order", orderId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0)); Assert.Equal(5, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
            Assert.Equal("CANCELLED", reader.GetString(0)); Assert.Equal("DELIVERY_FAILED_RETURNED", reader.GetString(1));
            Assert.Equal("VOID", reader.GetString(2)); Assert.Equal("DELIVERY_FAILED", reader.GetString(3));
            Assert.True(reader.GetBoolean(4)); Assert.Equal("CONSUMED", reader.GetString(5));
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
        }
    }

    [Fact]
    public async Task Concurrent_retry_and_return_stock_only_one_branch_wins()
    {
        var (customer, customerId, addressId) = await CreateCustomer();
        var (staffA, _) = await CreateRoleClient("STAFF");
        var (staffB, _) = await CreateRoleClient("STAFF");
        using (customer) using (staffA) using (staffB)
        {
            var (orderId, shipmentId, _) = await PreparePackedShipment(customer, staffA, customerId, addressId);
            using var handover = await Send(staffA, $"staff/shipments/{shipmentId}/handover");
            Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
            using var failed = await Send(staffA, $"staff/shipments/{shipmentId}/delivery-result",
                new { result = "FAILED", failureReason = "Không liên hệ được khách" });
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

            var responses = await Task.WhenAll(
                Send(staffA, $"staff/shipments/{shipmentId}/retry"),
                Send(staffB, $"staff/shipments/{shipmentId}/return-stock", new { receivedEvidence = "Phiếu nhập hoàn RT-001" }));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses) response.Dispose();

            await using var db = await fixture.OpenDatabaseAsync();
            await using var query = new NpgsqlCommand(
                "SELECT o.order_status,s.shipment_status,s.redelivery_count,s.return_stock_completed," +
                "(SELECT count(*) FROM store.inventory_movements WHERE source_id=o.id AND movement_type='RETURN_IN') " +
                "FROM store.sales_orders o JOIN store.shipments s ON s.order_id=o.id WHERE o.id=@order", db);
            query.Parameters.AddWithValue("order", orderId);
            await using var reader = await query.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            var retryWon = reader.GetString(0) == "FULFILLING";
            if (retryWon)
            {
                Assert.Equal("SHIPPING", reader.GetString(1)); Assert.Equal(1, reader.GetInt16(2));
                Assert.False(reader.GetBoolean(3)); Assert.Equal(0L, reader.GetInt64(4));
            }
            else
            {
                Assert.Equal("CANCELLED", reader.GetString(0)); Assert.Equal("DELIVERY_FAILED", reader.GetString(1));
                Assert.Equal(0, reader.GetInt16(2)); Assert.True(reader.GetBoolean(3)); Assert.Equal(1L, reader.GetInt64(4));
            }
        }
    }
}
