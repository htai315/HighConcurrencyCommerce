using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ReportingTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure reporting pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateRoleClient(string roleKey)
    {
        var id = Guid.NewGuid();
        var email = $"reporting_{roleKey}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key=@role", db);
        role.Parameters.AddWithValue("role", roleKey);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status) VALUES(@id,@role,@email,@hash,@name,'ACTIVE')", db);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email); insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("name", roleKey + " Reporting");
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

    [Fact]
    public async Task Reporting_and_task_endpoints_enforce_separation_of_duties()
    {
        using var guest = fixture.CreateClient();
        var (customer, _) = await CreateRoleClient("CUSTOMER");
        var (staff, _) = await CreateRoleClient("STAFF");
        var (admin, _) = await CreateRoleClient("ADMIN");
        using (customer) using (staff) using (admin)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/admin/reports/revenue")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin/reports/revenue")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/admin/reports/revenue")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/reports/revenue")).StatusCode);
            using var invalidRange = await admin.GetAsync("/api/v1/admin/reports/revenue?from=2024-01-01&to=2026-01-01");
            Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);
            Assert.Equal("INVALID_DATE_RANGE", (await invalidRange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/staff/dashboard/tasks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/staff/dashboard/tasks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/staff/dashboard/tasks")).StatusCode);
        }
    }

    [Fact]
    public async Task Revenue_counts_only_completed_exactly_verified_payments_and_low_stock_is_configured()
    {
        var (admin, adminId) = await CreateRoleClient("ADMIN");
        var (customer, customerId) = await CreateRoleClient("CUSTOMER");
        using (admin) using (customer)
        {
            var productId = Guid.NewGuid(); var lowSkuId = Guid.NewGuid(); var highSkuId = Guid.NewGuid();
            var validOrder = Guid.NewGuid(); var invalidOrder = Guid.NewGuid();
            var validPayment = Guid.NewGuid(); var invalidPayment = Guid.NewGuid();
            await using var db = await fixture.OpenDatabaseAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
                VALUES(@product,'Vợt báo cáo','PUBLISHED',@admin,@admin,now());
                INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) VALUES
                (@low,@product,@lowCode,'Low',100000,'ACTIVE'),(@high,@product,@highCode,'High',200000,'ACTIVE');
                INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision) VALUES
                (gen_random_uuid(),@low,3,3,0,0,0),(gen_random_uuid(),@high,10,10,0,0,0);
                INSERT INTO store.sales_orders(id,customer_id,order_number,payment_method,order_status,shipping_address_snapshot,total_amount,deadline_at,confirmed_at) VALUES
                (@validOrder,@customer,@validNumber,'COD','COMPLETED','{}',100000,now()+interval '1 day',now()),
                (@invalidOrder,@customer,@invalidNumber,'COD','COMPLETED','{}',200000,now()+interval '1 day',now());
                INSERT INTO store.payments(id,order_id,payment_method,expected_amount,payment_status,paid_at) VALUES
                (@validPayment,@validOrder,'COD',100000,'COD_COLLECTED',now()),
                (@invalidPayment,@invalidOrder,'COD',200000,'COD_COLLECTED',now());
                INSERT INTO store.payment_transactions(id,payment_id,provider,provider_transaction_id,amount,provider_paid_at,verified_at,transaction_status) VALUES
                (gen_random_uuid(),@validPayment,'COD',@validRef,100000,now(),now(),'VERIFIED'),
                (gen_random_uuid(),@invalidPayment,'COD',@invalidRef,199999,now(),now(),'VERIFIED');
                """, db);
            seed.Parameters.AddWithValue("product", productId); seed.Parameters.AddWithValue("admin", adminId);
            seed.Parameters.AddWithValue("low", lowSkuId); seed.Parameters.AddWithValue("high", highSkuId);
            seed.Parameters.AddWithValue("lowCode", "REPORT-LOW-" + lowSkuId.ToString("N")[..6]);
            seed.Parameters.AddWithValue("highCode", "REPORT-HIGH-" + highSkuId.ToString("N")[..6]);
            seed.Parameters.AddWithValue("validOrder", validOrder); seed.Parameters.AddWithValue("invalidOrder", invalidOrder);
            seed.Parameters.AddWithValue("customer", customerId);
            seed.Parameters.AddWithValue("validNumber", "HC-REPORT-" + validOrder.ToString("N")[..10]);
            seed.Parameters.AddWithValue("invalidNumber", "HC-REPORT-" + invalidOrder.ToString("N")[..10]);
            seed.Parameters.AddWithValue("validPayment", validPayment); seed.Parameters.AddWithValue("invalidPayment", invalidPayment);
            seed.Parameters.AddWithValue("validRef", "REPORT-" + validPayment.ToString("N"));
            seed.Parameters.AddWithValue("invalidRef", "REPORT-" + invalidPayment.ToString("N"));
            await seed.ExecuteNonQueryAsync();

            var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
            using var revenueResponse = await admin.GetAsync($"/api/v1/admin/reports/revenue?from={date}&to={date}");
            Assert.Equal(HttpStatusCode.OK, revenueResponse.StatusCode);
            var revenue = await revenueResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(100000m, revenue.GetProperty("revenue").GetDecimal());
            Assert.Equal(1, revenue.GetProperty("completedOrderCount").GetInt32());
            Assert.Equal(100000m, revenue.GetProperty("averageOrderValue").GetDecimal());

            using var lowStockResponse = await admin.GetAsync("/api/v1/admin/reports/low-stock");
            var lowStock = await lowStockResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5, lowStock.GetProperty("threshold").GetInt32());
            var ids = lowStock.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("productSkuId").GetGuid()).ToList();
            Assert.Contains(lowSkuId, ids); Assert.DoesNotContain(highSkuId, ids);
        }
    }

    [Fact]
    public async Task Audit_query_filters_and_pages_without_exposing_it_to_staff()
    {
        var (admin, adminId) = await CreateRoleClient("ADMIN");
        var (staff, _) = await CreateRoleClient("STAFF");
        using (admin) using (staff)
        {
            var entityA = Guid.NewGuid(); var entityB = Guid.NewGuid();
            await using var db = await fixture.OpenDatabaseAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO store.audit_logs(id,actor_id,actor_type,action,entity_type,entity_id,before_data,after_data,correlation_id,occurred_at) VALUES
                (gen_random_uuid(),@actor,'USER','REPORT_TEST_ACTION','SalesOrder',@a,'{}','{"status":"A"}',@correlation,now()),
                (gen_random_uuid(),@actor,'USER','OTHER_REPORT_ACTION','Product',@b,'{}','{}',@correlation,now());
                """, db);
            insert.Parameters.AddWithValue("actor", adminId); insert.Parameters.AddWithValue("a", entityA);
            insert.Parameters.AddWithValue("b", entityB); insert.Parameters.AddWithValue("correlation", "report-test-" + Guid.NewGuid().ToString("N"));
            await insert.ExecuteNonQueryAsync();

            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/admin/audit-logs")).StatusCode);
            using var response = await admin.GetAsync("/api/v1/admin/audit-logs?action=REPORT_TEST_ACTION&entityType=SalesOrder&page=1&pageSize=10");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
            var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal(entityA, item.GetProperty("entityId").GetGuid());
            Assert.Equal("REPORT_TEST_ACTION", item.GetProperty("action").GetString());
            Assert.Equal("ADMIN Reporting", item.GetProperty("actorName").GetString());
        }
    }
}
