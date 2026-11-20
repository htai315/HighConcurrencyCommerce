using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CarrierConsignmentTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";

    [Fact]
    public async Task Staff_consignment_routes_enforce_strict_RBAC()
    {
        using var guest = fixture.CreateClient();
        using var customer = (await CreateRoleSession("CUSTOMER")).Client;
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var staff = (await CreateRoleSession("STAFF")).Client;

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/v1/staff/carrier-consignments/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync("/api/v1/staff/carrier-consignments/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.GetAsync("/api/v1/staff/carrier-consignments/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await staff.GetAsync("/api/v1/staff/carrier-consignments/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await staff.GetAsync("/api/v1/staff/carrier-consignments/carriers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.GetAsync("/api/v1/staff/carrier-consignments/carriers")).StatusCode);

        var missingOrderId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.GetAsync($"/api/v1/orders/{missingOrderId}/tracking")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/v1/orders/{missingOrderId}/tracking")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.GetAsync($"/api/v1/orders/{missingOrderId}/tracking")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.GetAsync($"/api/v1/orders/{missingOrderId}/tracking")).StatusCode);
    }

    [Fact]
    public async Task Staff_can_create_sales_and_exchange_consignments_with_immutable_snapshots()
    {
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var staff = (await CreateRoleSession("STAFF")).Client;
        var customer = await CreateRoleSession("CUSTOMER");
        using var customerClient = customer.Client;
        var carrierId = await CreateActiveCarrier(admin);
        var salesShipmentId = await CreateSalesShipment(customer.UserId, "PACKED");
        var exchangeShipmentId = await CreateExchangeShipment(customer.UserId);

        using var salesResponse = await Send(staff,
            $"staff/carrier-consignments/sales-shipments/{salesShipmentId}", new
            {
                carrierId, trackingNumber = "T49-SALES-001", serviceCode = " EXPRESS "
            });
        Assert.Equal(HttpStatusCode.Created, salesResponse.StatusCode);
        var sales = await salesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SALES", sales.GetProperty("targetType").GetString());
        Assert.Equal("T49-SALES-001", sales.GetProperty("trackingNumber").GetString());
        Assert.Equal("EXPRESS", sales.GetProperty("serviceCode").GetString());
        Assert.Equal("Người nhận T49", sales.GetProperty("recipientName").GetString());
        Assert.Contains("Quận 1", sales.GetProperty("destination").GetString());
        Assert.Equal("SHIP_TEST", sales.GetProperty("shippingMethodCode").GetString());
        Assert.Equal("DRAFT", sales.GetProperty("consignmentStatus").GetString());
        Assert.Equal("CONSIGNMENT_CREATED",
            Assert.Single(sales.GetProperty("events").EnumerateArray()).GetProperty("eventType").GetString());

        using var exchangeResponse = await Send(staff,
            $"staff/carrier-consignments/exchange-shipments/{exchangeShipmentId}", new
            {
                carrierId, trackingNumber = "T49-EXCHANGE-001", serviceCode = "EXCHANGE"
            });
        Assert.Equal(HttpStatusCode.Created, exchangeResponse.StatusCode);
        var exchange = await exchangeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EXCHANGE", exchange.GetProperty("targetType").GetString());
        Assert.Equal(exchangeShipmentId, exchange.GetProperty("targetShipmentId").GetGuid());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var audit = new NpgsqlCommand("""
            SELECT count(*) FROM store.audit_logs
            WHERE entity_type='CarrierConsignment' AND action='CARRIER_CONSIGNMENT_CREATED'
              AND entity_id IN (@sales,@exchange)
            """, db);
        audit.Parameters.AddWithValue("sales", sales.GetProperty("id").GetGuid());
        audit.Parameters.AddWithValue("exchange", exchange.GetProperty("id").GetGuid());
        Assert.Equal(2L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Staff_can_attach_replace_download_and_cancel_label_before_handover()
    {
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var staff = (await CreateRoleSession("STAFF")).Client;
        var customer = await CreateRoleSession("CUSTOMER");
        using var customerClient = customer.Client;
        var carrierId = await CreateActiveCarrier(admin);
        var shipmentId = await CreateSalesShipment(customer.UserId, "PACKED");
        var consignment = await CreateConsignment(staff, carrierId, shipmentId, UniqueTracking("LABEL"));

        var firstBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfirst carrier label");
        using var firstResponse = await UploadLabel(staff, consignment.Id, 1, firstBytes, "label.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LABEL_READY", first.GetProperty("consignmentStatus").GetString());
        Assert.Equal(2, first.GetProperty("revision").GetInt64());
        Assert.Equal("application/pdf", first.GetProperty("label").GetProperty("contentType").GetString());

        using var download = await staff.GetAsync($"/api/v1/staff/carrier-consignments/{consignment.Id}/label");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(firstBytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("private", download.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        byte[] replacement = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
        using var replaceResponse = await UploadLabel(staff, consignment.Id, 2, replacement, "label.png", "image/png");
        Assert.Equal(HttpStatusCode.OK, replaceResponse.StatusCode);
        var replaced = await replaceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, replaced.GetProperty("revision").GetInt64());
        Assert.Equal(3, replaced.GetProperty("events").GetArrayLength());

        using var staleUpload = await UploadLabel(staff, consignment.Id, 2, firstBytes, "stale.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.Conflict, staleUpload.StatusCode);
        Assert.Equal("CONSIGNMENT_STALE",
            (await staleUpload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var cancel = await Send(staff, $"staff/carrier-consignments/{consignment.Id}/cancel",
            new { expectedRevision = 3L });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await cancel.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CANCELLED", cancelled.GetProperty("consignmentStatus").GetString());
        Assert.Equal(4, cancelled.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Concurrent_create_for_same_shipment_returns_one_created_one_conflict_no_500()
    {
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var firstStaff = (await CreateRoleSession("STAFF")).Client;
        using var secondStaff = (await CreateRoleSession("STAFF")).Client;
        var customer = await CreateRoleSession("CUSTOMER");
        using var customerClient = customer.Client;
        var carrierId = await CreateActiveCarrier(admin);
        var shipmentId = await CreateSalesShipment(customer.UserId, "PACKED");

        var responses = await Task.WhenAll(
            Send(firstStaff, $"staff/carrier-consignments/sales-shipments/{shipmentId}", new
                { carrierId, trackingNumber = UniqueTracking("SAME-A") }),
            Send(secondStaff, $"staff/carrier-consignments/sales-shipments/{shipmentId}", new
                { carrierId, trackingNumber = UniqueTracking("SAME-B") }));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
            Assert.Equal("CONSIGNMENT_ALREADY_EXISTS",
                (await responses.Single(x => x.StatusCode == HttpStatusCode.Conflict)
                    .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Duplicate_tracking_number_for_same_carrier_returns_409()
    {
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var staff = (await CreateRoleSession("STAFF")).Client;
        var customer = await CreateRoleSession("CUSTOMER");
        using var customerClient = customer.Client;
        var carrierId = await CreateActiveCarrier(admin);
        var firstShipment = await CreateSalesShipment(customer.UserId, "PACKED");
        var secondShipment = await CreateSalesShipment(customer.UserId, "PACKED");
        var tracking = UniqueTracking("DUP");
        _ = await CreateConsignment(staff, carrierId, firstShipment, tracking);

        using var duplicate = await Send(staff,
            $"staff/carrier-consignments/sales-shipments/{secondShipment}", new
            {
                carrierId, trackingNumber = tracking.ToLowerInvariant()
            });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("TRACKING_NUMBER_CONFLICT",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Manual_tracking_event_is_idempotent_and_rejects_changed_payload()
    {
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var staff = (await CreateRoleSession("STAFF")).Client;
        var customer = await CreateRoleSession("CUSTOMER");
        using var customerClient = customer.Client;
        var carrierId = await CreateActiveCarrier(admin);
        var shipmentId = await CreateSalesShipment(customer.UserId, "PACKED");
        var consignment = await CreateConsignment(staff, carrierId, shipmentId, UniqueTracking("EVENT"));
        var orderId = await GetOrderId(shipmentId);
        await MarkHandedOver(consignment.Id, shipmentId);
        var occurredAt = DateTime.UtcNow;
        var request = new
        {
            eventKey = "provider-pickup-001", eventType = "PICKED_UP", location = "Kho Quận 1",
            description = "Đã lấy hàng", occurredAt, expectedRevision = 1L
        };

        using var first = await Send(staff, $"staff/carrier-consignments/{consignment.Id}/events", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IN_TRANSIT", afterFirst.GetProperty("consignmentStatus").GetString());
        Assert.Equal(2, afterFirst.GetProperty("revision").GetInt64());

        using var replay = await Send(staff, $"staff/carrier-consignments/{consignment.Id}/events", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(2, (await replay.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("revision").GetInt64());

        using var conflict = await Send(staff, $"staff/carrier-consignments/{consignment.Id}/events", new
        {
            eventKey = "provider-pickup-001", eventType = "PICKED_UP", location = "Kho Quận 1",
            description = "Payload đã thay đổi", occurredAt, expectedRevision = 2L
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("TRACKING_IDEMPOTENCY_CONFLICT",
            (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var db = await fixture.OpenDatabaseAsync();
        await using var count = new NpgsqlCommand("""
            SELECT count(*) FROM store.carrier_tracking_events
            WHERE consignment_id=@id AND event_key='provider-pickup-001'
            """, db);
        count.Parameters.AddWithValue("id", consignment.Id);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

        using var trackingResponse = await customerClient.GetAsync($"/api/v1/orders/{orderId}/tracking");
        Assert.Equal(HttpStatusCode.OK, trackingResponse.StatusCode);
        var rawTracking = await trackingResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("actorId", rawTracking, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payloadHash", rawTracking, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("labelFileKey", rawTracking, StringComparison.OrdinalIgnoreCase);
        var tracking = JsonDocument.Parse(rawTracking).RootElement;
        Assert.Equal(consignment.Id, tracking.GetProperty("id").GetGuid());
        Assert.Equal("IN_TRANSIT", tracking.GetProperty("consignmentStatus").GetString());
        Assert.StartsWith("https://tracking.example.com/", tracking.GetProperty("trackingUrl").GetString());

        using var ordersResponse = await customerClient.GetAsync("/api/v1/orders?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, ordersResponse.StatusCode);
        var orders = await ordersResponse.Content.ReadFromJsonAsync<JsonElement>();
        var order = Assert.Single(orders.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == orderId);
        Assert.Equal(consignment.Id, order.GetProperty("tracking").GetProperty("consignmentId").GetGuid());

        var otherCustomer = await CreateRoleSession("CUSTOMER");
        using var otherClient = otherCustomer.Client;
        using var crossOwner = await otherClient.GetAsync($"/api/v1/orders/{orderId}/tracking");
        Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
        Assert.Equal("TRACKING_NOT_FOUND",
            (await crossOwner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var legacyShipmentId = await CreateSalesShipment(customer.UserId, "PACKED");
        var legacyOrderId = await GetOrderId(legacyShipmentId);
        using var legacyDetail = await customerClient.GetAsync($"/api/v1/orders/{legacyOrderId}");
        Assert.Equal(HttpStatusCode.OK, legacyDetail.StatusCode);
        Assert.Equal(JsonValueKind.Null,
            (await legacyDetail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tracking").ValueKind);
        using var legacyTracking = await customerClient.GetAsync($"/api/v1/orders/{legacyOrderId}/tracking");
        Assert.Equal(HttpStatusCode.NotFound, legacyTracking.StatusCode);

        await using var notification = new NpgsqlCommand("""
            SELECT count(*) FROM store.in_app_notifications
            WHERE recipient_id=@customer AND notification_type='ORDER_TRACKING_UPDATED'
            """, db);
        notification.Parameters.AddWithValue("customer", customer.UserId);
        Assert.Equal(1L, (long)(await notification.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Carrier_deactivate_and_create_consignment_serialize_without_partial_data()
    {
        using var admin = (await CreateRoleSession("ADMIN")).Client;
        using var staff = (await CreateRoleSession("STAFF")).Client;
        var customer = await CreateRoleSession("CUSTOMER");
        using var customerClient = customer.Client;
        var (carrierId, revision) = await CreateActiveCarrierWithRevision(admin);
        var shipmentId = await CreateSalesShipment(customer.UserId, "PACKED");

        var responses = await Task.WhenAll(
            Send(admin, $"admin/carriers/{carrierId}/deactivate", new { expectedRevision = revision }),
            Send(staff, $"staff/carrier-consignments/sales-shipments/{shipmentId}", new
                { carrierId, trackingNumber = UniqueTracking("DEACT") }));
        try
        {
            Assert.DoesNotContain(responses, x => x.StatusCode == HttpStatusCode.InternalServerError);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            if (responses[1].StatusCode == HttpStatusCode.Conflict)
                Assert.Equal("CARRIER_INACTIVE",
                    (await responses[1].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            await using var db = await fixture.OpenDatabaseAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM store.carrier_consignments WHERE sales_shipment_id=@shipment", db);
            count.Parameters.AddWithValue("shipment", shipmentId);
            var persisted = (long)(await count.ExecuteScalarAsync())!;
            Assert.Equal(responses[1].StatusCode == HttpStatusCode.Created ? 1L : 0L, persisted);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    private async Task<(Guid Id, long Revision)> CreateActiveCarrierWithRevision(HttpClient admin)
    {
        var code = "C" + Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();
        using var createdResponse = await Send(admin, "admin/carriers", new
        {
            code, name = "Carrier T49", trackingUrlTemplate = "https://tracking.example.com/{trackingNumber}"
        });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        using var activated = await Send(admin, $"admin/carriers/{id}/activate", new { expectedRevision = 1L });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        return (id, (await activated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64());
    }

    private async Task<Guid> CreateActiveCarrier(HttpClient admin) =>
        (await CreateActiveCarrierWithRevision(admin)).Id;

    private async Task<(Guid Id, long Revision)> CreateConsignment(
        HttpClient staff, Guid carrierId, Guid shipmentId, string tracking)
    {
        using var response = await Send(staff,
            $"staff/carrier-consignments/sales-shipments/{shipmentId}", new
            {
                carrierId, trackingNumber = tracking, serviceCode = "STANDARD"
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetGuid(), json.GetProperty("revision").GetInt64());
    }

    private async Task<Guid> CreateSalesShipment(Guid customerId, string status)
    {
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.sales_orders(
                id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,
                total_amount,subtotal_amount,voucher_discount_amount,shipping_fee,
                shipping_method_code_snapshot,shipping_method_name_snapshot,deadline_at,confirmed_at)
            VALUES(@order,@customer,@number,'COD','CONFIRMED',@address,120000,100000,0,20000,
                'SHIP_TEST','Giao hàng T49',now()+interval '1 day',now());
            INSERT INTO store.shipments(id,order_id,shipment_status) VALUES(@shipment,@order,@status);
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("number", "T49-" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant());
        command.Parameters.AddWithValue("address", NpgsqlDbType.Jsonb,
            "{\"recipientName\":\"Người nhận T49\",\"phone\":\"0901234567\",\"addressLine\":\"12 Nguyễn Huệ\",\"ward\":\"Quận 1\",\"province\":\"TP Hồ Chí Minh\"}");
        command.Parameters.AddWithValue("shipment", shipmentId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
        return shipmentId;
    }

    private async Task<Guid> CreateExchangeShipment(Guid customerId)
    {
        var orderId = Guid.NewGuid();
        var returnId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.sales_orders(
                id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,
                total_amount,subtotal_amount,voucher_discount_amount,shipping_fee,
                shipping_method_code_snapshot,shipping_method_name_snapshot,deadline_at,confirmed_at)
            VALUES(@order,@customer,@number,'COD','COMPLETED',@address,120000,100000,0,20000,
                'SHIP_TEST','Giao hàng T49',now()+interval '1 day',now());
            INSERT INTO store.order_return_requests(
                id,order_id,customer_id,return_status,resolution_type,reason_code,refund_amount,revision)
            VALUES(@return,@order,@customer,'REQUESTED','EXCHANGE','WRONG_SIZE',100000,0);
            INSERT INTO store.exchange_shipments(id,return_request_id,shipment_status)
            VALUES(@shipment,@return,'PENDING_PACKING');
            """, db);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("number", "T49-X-" + Guid.NewGuid().ToString("N")[..18].ToUpperInvariant());
        command.Parameters.AddWithValue("address", NpgsqlDbType.Jsonb,
            "{\"recipientName\":\"Người nhận T49\",\"phone\":\"0901234567\",\"addressLine\":\"12 Nguyễn Huệ\",\"ward\":\"Quận 1\",\"province\":\"TP Hồ Chí Minh\"}");
        command.Parameters.AddWithValue("return", returnId);
        command.Parameters.AddWithValue("shipment", shipmentId);
        await command.ExecuteNonQueryAsync();
        return shipmentId;
    }

    private async Task MarkHandedOver(Guid consignmentId, Guid shipmentId)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE store.shipments SET shipment_status='SHIPPING',shipped_at=now() WHERE id=@shipment;
            UPDATE store.carrier_consignments
            SET consignment_status='HANDED_OVER',handed_over_at=now() WHERE id=@consignment;
            """, db);
        command.Parameters.AddWithValue("shipment", shipmentId);
        command.Parameters.AddWithValue("consignment", consignmentId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> GetOrderId(Guid shipmentId)
    {
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT order_id FROM store.shipments WHERE id=@shipment", db);
        command.Parameters.AddWithValue("shipment", shipmentId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleSession(string role)
    {
        var client = fixture.CreateClient();
        var email = $"carrier_consignment_{role}_{Guid.NewGuid():N}@example.com";
        using var register = await Send(client, "auth/register", new
        {
            email, password = Password, confirmPassword = Password, fullName = $"{role} Carrier Test"
        });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleUpdate = new NpgsqlCommand("""
            UPDATE store.user_accounts
            SET role_id=(SELECT id FROM store.roles WHERE role_key=@role), email_verified_at=now()
            WHERE lower(email)=lower(@email)
            RETURNING id
            """, db);
        roleUpdate.Parameters.AddWithValue("role", role);
        roleUpdate.Parameters.AddWithValue("email", email);
        var userId = (Guid)(await roleUpdate.ExecuteScalarAsync())!;
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, userId);
    }

    private static string UniqueTracking(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 17, 100)].ToUpperInvariant();

    private static async Task<HttpResponseMessage> UploadLabel(
        HttpClient client, Guid id, long expectedRevision, byte[] bytes, string fileName, string contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/staff/carrier-consignments/{id}/label");
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        content.Add(new StringContent(expectedRevision.ToString()), "expectedRevision");
        request.Content = content;
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if ((method ?? HttpMethod.Post) != HttpMethod.Get)
            request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }
}
