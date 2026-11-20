using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class PayOsReconciliationTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "PayOS Reconciliation Pass 123!";

    [Fact]
    public async Task Queue_enforces_admin_RBAC_and_claim_resolve_workflow()
    {
        using var guest = fixture.CreateClient();
        using var customer = await CreateUser("CUSTOMER");
        using var admin = await CreateUser("ADMIN");
        var caseId = await SeedCase("AMOUNT_MISMATCH", "PENDING", 120_000m, 130_000m);

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/admin/payos-reconciliation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/payos-reconciliation")).StatusCode);
        using var queue = await admin.GetAsync("/api/v1/admin/payos-reconciliation?status=OPEN");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var body = await queue.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == caseId);

        using var claim = await Send(admin, $"admin/payos-reconciliation/{caseId}/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal("IN_REVIEW", (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("caseStatus").GetString());
        using var resolve = await Send(admin, $"admin/payos-reconciliation/{caseId}/resolve",
            new { note = "Đã xác minh sai lệch do payload thử nghiệm; không phát sinh tiền thực nhận." });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal("RESOLVED", (await resolve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("caseStatus").GetString());
    }

    [Fact]
    public async Task Concurrent_manual_refund_records_exactly_one_evidence_and_audit()
    {
        using var admin = await CreateUser("ADMIN");
        var caseId = await SeedCase("LATE_PAID", "LATE_PAID", 250_000m, 250_000m);
        using var claim = await Send(admin, $"admin/payos-reconciliation/{caseId}/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var csrf = await Csrf(admin);

        Task<HttpResponseMessage> Refund() => Send(admin, $"admin/payos-reconciliation/{caseId}/manual-refund",
            new { evidenceReference = $"PAYOS-REFUND-{caseId:N}", note = "Đã hoàn toàn bộ khoản tiền đến muộn qua ngân hàng." }, csrf);
        var responses = await Task.WhenAll(Refund(), Refund());
        try
        {
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in responses) response.Dispose(); }

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT c.case_status,p.payment_status,
                   (SELECT count(*) FROM store.manual_refunds m JOIN store.payment_transactions t ON t.id=m.transaction_id WHERE t.payment_id=p.id),
                   (SELECT count(*) FROM store.audit_logs a WHERE a.entity_id=c.id AND a.action='PAYOS_MANUAL_REFUND_RECORDED')
            FROM store.payment_reconciliation_cases c JOIN store.payments p ON p.id=c.payment_id
            WHERE c.id=@case
            """, db);
        command.Parameters.AddWithValue("case", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("RESOLVED", reader.GetString(0));
        Assert.Equal("REFUNDED", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task Whitespace_only_note_and_refund_evidence_are_rejected()
    {
        using var admin = await CreateUser("ADMIN");
        var resolveCase = await SeedCase("AMOUNT_MISMATCH", "PENDING", 120_000m, 130_000m);
        using (var claim = await Send(admin, $"admin/payos-reconciliation/{resolveCase}/claim", null))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var resolve = await Send(admin, $"admin/payos-reconciliation/{resolveCase}/resolve",
            new { note = "     " });
        Assert.Equal(HttpStatusCode.BadRequest, resolve.StatusCode);

        var refundCase = await SeedCase("LATE_PAID", "LATE_PAID", 250_000m, 250_000m);
        using (var claim = await Send(admin, $"admin/payos-reconciliation/{refundCase}/claim", null))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var refund = await Send(admin, $"admin/payos-reconciliation/{refundCase}/manual-refund",
            new { evidenceReference = "   ", note = "     " });
        Assert.Equal(HttpStatusCode.BadRequest, refund.StatusCode);
    }

    [Fact]
    public async Task Expired_claim_lease_can_be_taken_over_by_another_admin()
    {
        using var firstAdmin = await CreateUser("ADMIN");
        using var secondAdmin = await CreateUser("ADMIN");
        var caseId = await SeedCase("AMOUNT_MISMATCH", "PENDING", 120_000m, 130_000m);
        using (var claim = await Send(firstAdmin, $"admin/payos-reconciliation/{caseId}/claim", null))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var expire = new NpgsqlCommand(
                         "UPDATE store.payment_reconciliation_cases SET assigned_at=now()-interval '31 minutes' WHERE id=@id", db))
        {
            expire.Parameters.AddWithValue("id", caseId);
            await expire.ExecuteNonQueryAsync();
        }

        using var takeover = await Send(secondAdmin, $"admin/payos-reconciliation/{caseId}/claim", null);
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        Assert.Equal("IN_REVIEW", (await takeover.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("caseStatus").GetString());

        await using var verifyDb = await fixture.OpenDatabaseAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT count(*) FROM store.audit_logs
            WHERE entity_id=@id AND action='PAYOS_RECONCILIATION_REASSIGNED'
            """, verifyDb);
        verify.Parameters.AddWithValue("id", caseId);
        Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Refunding_pending_amount_mismatch_closes_order_and_releases_reserved_stock()
    {
        using var admin = await CreateUser("ADMIN");
        var seeded = await SeedPendingMismatchCase();
        using (var claim = await Send(admin, $"admin/payos-reconciliation/{seeded.CaseId}/claim", null))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var refund = await Send(admin, $"admin/payos-reconciliation/{seeded.CaseId}/manual-refund",
            new { evidenceReference = $"MISMATCH-{seeded.CaseId:N}", note = "Đã hoàn toàn bộ khoản thanh toán sai số tiền." });
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT o.order_status,o.closed_reason,p.payment_status,r.reservation_status,
                   b.available_qty,b.reserved_qty,
                   (SELECT count(*) FROM store.inventory_movements m
                    WHERE m.source_id=o.id AND m.movement_type='RELEASE'),
                   (SELECT count(*) FROM store.manual_refunds m
                    WHERE m.reconciliation_case_id=@case)
            FROM store.sales_orders o
            JOIN store.payments p ON p.order_id=o.id
            JOIN store.stock_reservations r ON r.order_id=o.id
            JOIN store.inventory_balances b ON b.product_sku_id=@sku
            WHERE o.id=@order
            """, db);
        command.Parameters.AddWithValue("case", seeded.CaseId);
        command.Parameters.AddWithValue("order", seeded.OrderId);
        command.Parameters.AddWithValue("sku", seeded.SkuId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("EXPIRED", reader.GetString(0));
        Assert.Equal("PAYOS_RECONCILIATION_REFUNDED", reader.GetString(1));
        Assert.Equal("REFUNDED", reader.GetString(2));
        Assert.Equal("RELEASED", reader.GetString(3));
        Assert.Equal(10, reader.GetInt32(4));
        Assert.Equal(0, reader.GetInt32(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal(1L, reader.GetInt64(7));
    }

    [Fact]
    public async Task Refunding_duplicate_collection_keeps_original_successful_payment()
    {
        using var admin = await CreateUser("ADMIN");
        var caseId = await SeedCase("DUPLICATE_COLLECTION", "SUCCESS", 250_000m, 250_000m);
        using (var claim = await Send(admin, $"admin/payos-reconciliation/{caseId}/claim", null))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var refund = await Send(admin, $"admin/payos-reconciliation/{caseId}/manual-refund",
            new { evidenceReference = $"DUPLICATE-{caseId:N}", note = "Đã hoàn khoản thu trùng và giữ nguyên giao dịch gốc." });
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT p.payment_status,c.case_status,
                   (SELECT count(*) FROM store.manual_refunds m WHERE m.reconciliation_case_id=c.id)
            FROM store.payment_reconciliation_cases c
            JOIN store.payments p ON p.id=c.payment_id WHERE c.id=@case
            """, db);
        command.Parameters.AddWithValue("case", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SUCCESS", reader.GetString(0));
        Assert.Equal("RESOLVED", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    private async Task<HttpClient> CreateUser(string role)
    {
        var client = fixture.CreateClient();
        var email = $"payos-recon-{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.invalid";
        using var register = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = $"PayOS {role}" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key=@role),email_verified_at=now() WHERE email=@email", db);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("email", email);
        await command.ExecuteNonQueryAsync();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<Guid> SeedCase(string reason, string paymentStatus, decimal expected, decimal received)
    {
        var caseId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(id,role_id,email,full_name,account_status,email_verified_at)
            VALUES(@customer,(SELECT id FROM store.roles WHERE role_key='CUSTOMER'),@email,'Reconciliation Customer','ACTIVE',now());
            INSERT INTO store.sales_orders(id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,subtotal_amount,voucher_discount_amount,shipping_fee,total_amount,deadline_at,closed_reason)
            VALUES(@order,@customer,@number,'PAYOS','EXPIRED','{}',@expected,0,0,@expected,now()+interval '15 minutes','PAYMENT_EXPIRED');
            INSERT INTO store.payments(id,order_id,payment_method,expected_amount,payment_status,paid_at)
            VALUES(@payment,@order,'PAYOS',@expected,@status,now());
            INSERT INTO store.payment_transactions(id,payment_id,provider,provider_transaction_id,amount,provider_paid_at,verified_at,transaction_status)
            VALUES(@transaction,@payment,'PAYOS',@reference,@received,now(),now(),'RECONCILIATION_REQUIRED');
            INSERT INTO store.payment_reconciliation_cases(id,payment_id,transaction_id,reason,case_status)
            VALUES(@case,@payment,@transaction,@reason,'OPEN');
            """, db);
        command.Parameters.AddWithValue("case", caseId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("payment", paymentId);
        command.Parameters.AddWithValue("transaction", transactionId);
        command.Parameters.AddWithValue("email", $"recon-customer-{customerId:N}@example.invalid");
        command.Parameters.AddWithValue("number", $"HC-RECON-{orderId:N}"[..21]);
        command.Parameters.AddWithValue("reference", $"PAYOS-RECON-{caseId:N}");
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("status", paymentStatus);
        command.Parameters.AddWithValue("expected", expected);
        command.Parameters.AddWithValue("received", received);
        await command.ExecuteNonQueryAsync();
        return caseId;
    }

    private async Task<(Guid CaseId, Guid OrderId, Guid SkuId)> SeedPendingMismatchCase()
    {
        var caseId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(id,role_id,email,full_name,account_status,email_verified_at)
            VALUES(@customer,(SELECT id FROM store.roles WHERE role_key='CUSTOMER'),@email,'Mismatch Customer','ACTIVE',now());
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Mismatch product','PUBLISHED',@customer,@customer,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@skuCode,'Test',1000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,8,2,0,0);
            INSERT INTO store.sales_orders(id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,subtotal_amount,voucher_discount_amount,shipping_fee,total_amount,deadline_at)
            VALUES(@order,@customer,@number,'PAYOS','PENDING_PAYMENT','{}',2000,0,0,2000,now()+interval '15 minutes');
            INSERT INTO store.sales_order_items(id,order_id,product_sku_id,product_title_snapshot,sku_code_snapshot,sku_option_label_snapshot,quantity,unit_price)
            VALUES(@item,@order,@sku,'Mismatch product',@skuCode,'Test',2,1000);
            INSERT INTO store.payments(id,order_id,payment_method,expected_amount,payment_status)
            VALUES(@payment,@order,'PAYOS',2000,'PENDING');
            INSERT INTO store.stock_reservations(id,order_id,reservation_status,reserved_at,expires_at)
            VALUES(@reservation,@order,'HELD',now(),now()+interval '15 minutes');
            INSERT INTO store.stock_reservation_lines(id,reservation_id,order_item_id,product_sku_id,quantity,line_status)
            VALUES(gen_random_uuid(),@reservation,@item,@sku,2,'HELD');
            INSERT INTO store.payment_transactions(id,payment_id,provider,provider_transaction_id,amount,provider_paid_at,verified_at,transaction_status)
            VALUES(@transaction,@payment,'PAYOS',@reference,1999,now(),now(),'RECONCILIATION_REQUIRED');
            INSERT INTO store.payment_reconciliation_cases(id,payment_id,transaction_id,reason,case_status)
            VALUES(@case,@payment,@transaction,'AMOUNT_MISMATCH','OPEN');
            """, db);
        command.Parameters.AddWithValue("case", caseId);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("sku", skuId);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("reservation", reservationId);
        command.Parameters.AddWithValue("payment", paymentId);
        command.Parameters.AddWithValue("transaction", transactionId);
        command.Parameters.AddWithValue("email", $"mismatch-{customerId:N}@example.invalid");
        command.Parameters.AddWithValue("number", $"HC-MISMATCH-{orderId:N}"[..21]);
        command.Parameters.AddWithValue("skuCode", "MIS-" + skuId.ToString("N")[..10].ToUpperInvariant());
        command.Parameters.AddWithValue("reference", $"PAYOS-MISMATCH-{caseId:N}");
        await command.ExecuteNonQueryAsync();
        return (caseId, orderId, skuId);
    }

    private static async Task<string> Csrf(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf ?? await Csrf(client));
        return await client.SendAsync(request);
    }
}
