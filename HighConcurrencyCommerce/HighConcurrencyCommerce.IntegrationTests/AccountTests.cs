using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class AccountTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A long test password 123!";
    private static string Email() => Guid.NewGuid().ToString("N") + "@example.invalid";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object body, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task<HttpClient> CreateCustomerClient(CatalogFixture fixture, string? customEmail = null)
    {
        var client = fixture.CreateClient();
        var email = customEmail ?? Email();
        using var reg = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Test Customer" });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task Guest_and_staff_cannot_access_customer_account_endpoints()
    {
        using var guest = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/account/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/account/addresses")).StatusCode);

        // Staff login
        await using var db = await fixture.OpenDatabaseAsync();
        var staffEmail = Email();
        await using (var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = 'STAFF'", db))
        {
            var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
            var hash = hasher.HashPassword(new object(), Password);
            await using var insertCmd = new NpgsqlCommand(
                "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status) VALUES (gen_random_uuid(), @roleId, @email, @hash, 'Staff User', 'ACTIVE')", db);
            insertCmd.Parameters.AddWithValue("roleId", roleId);
            insertCmd.Parameters.AddWithValue("email", staffEmail);
            insertCmd.Parameters.AddWithValue("hash", hash);
            await insertCmd.ExecuteNonQueryAsync();
        }

        using var staffClient = fixture.CreateClient();
        using var staffLogin = await Send(staffClient, "auth/login", new { email = staffEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, staffLogin.StatusCode);

        // Staff is forbidden from customer account routes
        Assert.Equal(HttpStatusCode.Forbidden, (await staffClient.GetAsync("/api/v1/account/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staffClient.GetAsync("/api/v1/account/addresses")).StatusCode);
    }

    [Fact]
    public async Task Profile_get_and_update_with_validation()
    {
        using var client = await CreateCustomerClient(fixture);

        // GET profile
        var profileRes = await client.GetAsync("/api/v1/account/profile");
        Assert.Equal(HttpStatusCode.OK, profileRes.StatusCode);
        var profile = await profileRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Test Customer", profile.GetProperty("fullName").GetString());
        Assert.Equal("CUSTOMER", profile.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("phone").ValueKind);

        // Update profile valid
        using var updateRes = await Send(client, "account/profile", new { fullName = "Nguyễn Văn Cầu Lông", phone = "0987654321" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nguyễn Văn Cầu Lông", updated.GetProperty("fullName").GetString());
        Assert.Equal("0987654321", updated.GetProperty("phone").GetString());

        // Update profile empty name -> 400
        using var invalidNameRes = await Send(client, "account/profile", new { fullName = "", phone = "0987654321" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, invalidNameRes.StatusCode);

        // Update profile invalid phone -> 400 (too short < 8 digits or letters)
        using var invalidPhoneRes = await Send(client, "account/profile", new { fullName = "Valid Name", phone = "invalid_phone_123" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPhoneRes.StatusCode);

        using var shortPhoneRes = await Send(client, "account/profile", new { fullName = "Valid Name", phone = "1234567" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, shortPhoneRes.StatusCode);

        using var longPhoneRes = await Send(client, "account/profile", new { fullName = "Valid Name", phone = "1234567890123456" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, longPhoneRes.StatusCode);

        // Update profile with valid E.164 phone (+84901234567) -> 200
        using var e164PhoneRes = await Send(client, "account/profile", new { fullName = "Nguyễn Văn Cầu Lông", phone = "+84901234567" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, e164PhoneRes.StatusCode);
        var e164User = await e164PhoneRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("+84901234567", e164User.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Address_crud_and_single_default_guarantee()
    {
        using var client = await CreateCustomerClient(fixture);

        // 1. Initial list is empty
        var emptyListRes = await client.GetAsync("/api/v1/account/addresses");
        Assert.Equal(HttpStatusCode.OK, emptyListRes.StatusCode);
        var emptyList = await emptyListRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, emptyList.GetArrayLength());

        // 2. Create first address with isDefault = false -> promoted to default automatically
        var firstPayload = new
        {
            recipientName = "Người Nhận 1",
            phone = "0901234567",
            addressLine = "123 Đường Cầu Lông",
            ward = "Phường 1",
            province = "TP. Hồ Chí Minh",
            isDefault = false
        };
        using var createFirstRes = await Send(client, "account/addresses", firstPayload);
        Assert.Equal(HttpStatusCode.Created, createFirstRes.StatusCode);
        var firstAddr = await createFirstRes.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstAddr.GetProperty("id").GetString()!;
        Assert.True(firstAddr.GetProperty("isDefault").GetBoolean());

        // Verify Location header matches /api/v1/account/addresses/{id} contract exactly (not ?id=...)
        Assert.NotNull(createFirstRes.Headers.Location);
        Assert.EndsWith($"/api/v1/account/addresses/{firstId}", createFirstRes.Headers.Location.ToString());

        // Follow Location header to verify GET /addresses/{id}
        using var getFirstRes = await client.GetAsync(createFirstRes.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, getFirstRes.StatusCode);
        var fetchedFirst = await getFirstRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstId, fetchedFirst.GetProperty("id").GetString());
        Assert.Equal("Người Nhận 1", fetchedFirst.GetProperty("recipientName").GetString());

        // 3. Create second address with isDefault = true -> second becomes default, first becomes non-default
        var secondPayload = new
        {
            recipientName = "Người Nhận 2",
            phone = "0909876543",
            addressLine = "456 Đường Sân Cầu",
            ward = "Phường 2",
            province = "TP. Hà Nội",
            isDefault = true
        };
        using var createSecondRes = await Send(client, "account/addresses", secondPayload);
        Assert.Equal(HttpStatusCode.Created, createSecondRes.StatusCode);
        var secondAddr = await createSecondRes.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondAddr.GetProperty("id").GetString()!;
        Assert.True(secondAddr.GetProperty("isDefault").GetBoolean());

        // Verify list has 2 items, second is default and appears first
        var listRes = await client.GetAsync("/api/v1/account/addresses");
        var list = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(secondId, list[0].GetProperty("id").GetString());
        Assert.True(list[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal(firstId, list[1].GetProperty("id").GetString());
        Assert.False(list[1].GetProperty("isDefault").GetBoolean());

        // 4. Update first address to isDefault = true -> first becomes default, second becomes non-default
        using var updateFirstRes = await Send(client, $"account/addresses/{firstId}", new
        {
            recipientName = "Người Nhận 1 Cập Nhật",
            phone = "0901234567",
            addressLine = "123 Đường Cầu Lông Đổi Số",
            ward = "Phường 1",
            province = "TP. Hồ Chí Minh",
            isDefault = true
        }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updateFirstRes.StatusCode);

        // Verify list again
        var listRes2 = await client.GetAsync("/api/v1/account/addresses");
        var list2 = await listRes2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstId, list2[0].GetProperty("id").GetString());
        Assert.True(list2[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal(secondId, list2[1].GetProperty("id").GetString());
        Assert.False(list2[1].GetProperty("isDefault").GetBoolean());

        // 5. Delete second address
        using var deleteSecondReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/account/addresses/{secondId}");
        deleteSecondReq.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        using var deleteSecondRes = await client.SendAsync(deleteSecondReq);
        Assert.Equal(HttpStatusCode.NoContent, deleteSecondRes.StatusCode);

        // Verify list now has 1 item
        var finalListRes = await client.GetAsync("/api/v1/account/addresses");
        var finalList = await finalListRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, finalList.GetArrayLength());
        Assert.Equal(firstId, finalList[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Owner_isolation_blocks_cross_customer_access()
    {
        using var customerA = await CreateCustomerClient(fixture);
        using var customerB = await CreateCustomerClient(fixture);

        // Customer A creates address
        using var createRes = await Send(customerA, "account/addresses", new
        {
            recipientName = "Customer A Address",
            phone = "0901111111",
            addressLine = "A Street",
            province = "HCM",
            isDefault = true
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var addrA = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var addrAId = addrA.GetProperty("id").GetString()!;

        // Customer B cannot see A's address in list
        var bListRes = await customerB.GetAsync("/api/v1/account/addresses");
        var bList = await bListRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, bList.GetArrayLength());

        // Customer B cannot get A's address detail -> 404
        var bGetRes = await customerB.GetAsync($"/api/v1/account/addresses/{addrAId}");
        Assert.Equal(HttpStatusCode.NotFound, bGetRes.StatusCode);

        // Customer B cannot patch A's address -> 404
        using var patchRes = await Send(customerB, $"account/addresses/{addrAId}", new
        {
            recipientName = "Hacker",
            phone = "0909999999",
            addressLine = "Hack",
            province = "Hack",
            isDefault = true
        }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.NotFound, patchRes.StatusCode);

        // Customer B cannot delete A's address -> 404
        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/account/addresses/{addrAId}");
        delReq.Headers.Add("X-CSRF-TOKEN", await Csrf(customerB));
        using var delRes = await customerB.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NotFound, delRes.StatusCode);

        // A's address is untouched
        var aListRes = await customerA.GetAsync("/api/v1/account/addresses");
        var aList = await aListRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, aList.GetArrayLength());
        Assert.Equal("Customer A Address", aList[0].GetProperty("recipientName").GetString());
    }

    [Fact]
    public async Task Delete_address_in_use_by_order_returns_conflict()
    {
        var email = Email();
        using var client = await CreateCustomerClient(fixture, email);

        using var createRes = await Send(client, "account/addresses", new
        {
            recipientName = "Recipient For Order",
            phone = "0901234567",
            addressLine = "Address With Order",
            province = "HCM",
            isDefault = true
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var addr = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var addrId = Guid.Parse(addr.GetProperty("id").GetString()!);

        // Simulate sales order referencing this address
        await using var db = await fixture.OpenDatabaseAsync();
        await using var userCmd = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE email = @email", db);
        userCmd.Parameters.AddWithValue("email", email);
        var userId = (Guid)(await userCmd.ExecuteScalarAsync())!;

        await using var orderCmd = new NpgsqlCommand(
            @"INSERT INTO store.sales_orders 
              (id, customer_id, shipping_address_id, order_number, payment_method, order_status, 
               shipping_address_snapshot, total_amount, deadline_at)
              VALUES 
              (gen_random_uuid(), @customerId, @addrId, @orderNo, 'COD', 'PENDING_COD_CONFIRMATION',
               '{}'::jsonb, 130000, now() + interval '24 hours')", db);
        orderCmd.Parameters.AddWithValue("customerId", userId);
        orderCmd.Parameters.AddWithValue("addrId", addrId);
        orderCmd.Parameters.AddWithValue("orderNo", "ORD-" + Guid.NewGuid().ToString("N")[..8]);
        await orderCmd.ExecuteNonQueryAsync();

        // Customer attempts to delete address in use -> 409 Conflict
        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/account/addresses/{addrId}");
        delReq.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        using var delRes = await client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.Conflict, delRes.StatusCode);
        var problem = await delRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ADDRESS_IN_USE", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_create_address_guarantees_single_default()
    {
        var email = Email();
        using var client1 = await CreateCustomerClient(fixture, email);
        using var client2 = fixture.CreateClient();
        // Login on second client with same account
        using var login2 = await Send(client2, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login2.StatusCode);

        // Run 5 concurrent requests all trying to create an address with isDefault = true
        var tasks = Enumerable.Range(1, 5).Select(i =>
        {
            var c = i % 2 == 0 ? client1 : client2;
            return Send(c, "account/addresses", new
            {
                recipientName = $"Parallel {i}",
                phone = "090000000" + i,
                addressLine = $"{i} Concurrent Road",
                province = "HCM",
                isDefault = true
            });
        }).ToArray();

        var responses = await Task.WhenAll(tasks);
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            r.Dispose();
        }

        // Verify in DB that exactly ONE address is default for this customer
        await using var db = await fixture.OpenDatabaseAsync();
        await using var query = new NpgsqlCommand(
            "SELECT count(*) FROM store.customer_addresses a JOIN store.user_accounts u ON a.customer_id = u.id WHERE u.email = @email AND a.is_default = true", db);
        query.Parameters.AddWithValue("email", email);
        var defaultCount = (long)(await query.ExecuteScalarAsync())!;
        Assert.Equal(1L, defaultCount);

        // And total addresses created is 5
        await using var totalQuery = new NpgsqlCommand(
            "SELECT count(*) FROM store.customer_addresses a JOIN store.user_accounts u ON a.customer_id = u.id WHERE u.email = @email", db);
        totalQuery.Parameters.AddWithValue("email", email);
        var totalCount = (long)(await totalQuery.ExecuteScalarAsync())!;
        Assert.Equal(5L, totalCount);
    }
}
