using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class NotificationTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure notification pass 123!";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + path);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateCustomer()
    {
        var id = Guid.NewGuid();
        var email = $"notification_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var role = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='CUSTOMER'", db);
        var roleId = (Guid)(await role.ExecuteScalarAsync())!;
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status) VALUES(@id,@role,@email,@hash,'Notification Customer','ACTIVE')", db);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("role", roleId);
        insert.Parameters.AddWithValue("email", email); insert.Parameters.AddWithValue("hash", hash);
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
    public async Task Notification_feed_is_authenticated_recipient_only_and_read_is_idempotent()
    {
        using var guest = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/notifications")).StatusCode);
        var (customerA, customerAId) = await CreateCustomer();
        var (customerB, customerBId) = await CreateCustomer();
        using (customerA) using (customerB)
        {
            var notificationA = Guid.NewGuid();
            var notificationB = Guid.NewGuid();
            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO store.in_app_notifications(id,recipient_id,event_key,notification_type,title,body) VALUES " +
                "(@a,@customerA,@eventA,'ORDER_CONFIRMED','Đơn đã xác nhận','Nội dung A')," +
                "(@b,@customerB,@eventB,'ORDER_SHIPPING','Đơn đang giao','Nội dung B')", db))
            {
                insert.Parameters.AddWithValue("a", notificationA); insert.Parameters.AddWithValue("customerA", customerAId);
                insert.Parameters.AddWithValue("eventA", "test:" + notificationA.ToString("N"));
                insert.Parameters.AddWithValue("b", notificationB); insert.Parameters.AddWithValue("customerB", customerBId);
                insert.Parameters.AddWithValue("eventB", "test:" + notificationB.ToString("N"));
                await insert.ExecuteNonQueryAsync();
            }

            using var mine = await customerA.GetAsync("/api/v1/notifications?limit=10");
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            Assert.True(mine.Headers.CacheControl?.NoStore);
            var feed = await mine.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, feed.GetProperty("unreadCount").GetInt32());
            var item = Assert.Single(feed.GetProperty("items").EnumerateArray());
            Assert.Equal(notificationA, item.GetProperty("id").GetGuid());
            Assert.DoesNotContain("Nội dung B", feed.ToString());

            using var crossOwner = await Post(customerA, $"notifications/{notificationB}/read");
            Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
            using var firstRead = await Post(customerA, $"notifications/{notificationA}/read");
            Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
            var firstReadAt = (await firstRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readAt").GetDateTime();
            using var replayRead = await Post(customerA, $"notifications/{notificationA}/read");
            Assert.Equal(HttpStatusCode.OK, replayRead.StatusCode);
            Assert.Equal(firstReadAt, (await replayRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readAt").GetDateTime());

            using var unread = await customerA.GetAsync("/api/v1/notifications?unreadOnly=true");
            var unreadFeed = await unread.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, unreadFeed.GetProperty("unreadCount").GetInt32());
            Assert.Empty(unreadFeed.GetProperty("items").EnumerateArray());
        }
    }
}
