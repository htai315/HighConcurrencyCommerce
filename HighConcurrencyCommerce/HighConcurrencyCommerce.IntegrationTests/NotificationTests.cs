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
            "INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,email_verified_at) VALUES(@id,@role,@email,@hash,'Notification Customer','ACTIVE',now())", db);
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
                "INSERT INTO store.in_app_notifications(id,recipient_id,event_key,notification_type,title,body,action_url) VALUES " +
                "(@a,@customerA,@eventA,'WISHLIST_BACK_IN_STOCK','Đã có hàng','Nội dung A','/products/11111111-1111-1111-1111-111111111111')," +
                "(@b,@customerB,@eventB,'ORDER_SHIPPING','Đơn đang giao','Nội dung B',NULL)", db))
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
            Assert.Equal("/products/11111111-1111-1111-1111-111111111111", item.GetProperty("actionUrl").GetString());
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

    [Fact]
    public async Task Notification_feed_supports_paging_and_server_unread_filtering_beyond_50_items()
    {
        var (customer, customerId) = await CreateCustomer();
        using (customer)
        {
            await using var db = await fixture.OpenDatabaseAsync();
            var baseTime = DateTime.UtcNow.AddMinutes(-100);
            for (var i = 1; i <= 55; i++)
            {
                var id = Guid.NewGuid();
                var readAt = i <= 50 ? baseTime.AddMinutes(i) : (DateTime?)null;
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO store.in_app_notifications(id,recipient_id,event_key,notification_type,title,body,read_at,created_at,updated_at) " +
                    "VALUES (@id,@recipient,@event,'ORDER_UPDATE',@title,@body,@readAt,@createdAt,@updatedAt)", db);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("recipient", customerId);
                insert.Parameters.AddWithValue("event", $"event:{id:N}");
                insert.Parameters.AddWithValue("title", $"Thông báo #{i}");
                insert.Parameters.AddWithValue("body", $"Nội dung #{i}");
                insert.Parameters.AddWithValue("readAt", readAt.HasValue ? (object)readAt.Value : DBNull.Value);
                insert.Parameters.AddWithValue("createdAt", baseTime.AddMinutes(i));
                insert.Parameters.AddWithValue("updatedAt", baseTime.AddMinutes(i));
                await insert.ExecuteNonQueryAsync();
            }

            // 1. Top 50 notifications are all read, hasMore should be true
            using var top50Response = await customer.GetAsync("/api/v1/notifications?limit=50");
            Assert.Equal(HttpStatusCode.OK, top50Response.StatusCode);
            var top50Feed = await top50Response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5, top50Feed.GetProperty("unreadCount").GetInt32());
            Assert.True(top50Feed.GetProperty("hasMore").GetBoolean());
            var top50Items = top50Feed.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(50, top50Items.Count);

            // 2. Querying with unreadOnly=true returns the 5 unread items even though they are older
            using var unreadResponse = await customer.GetAsync("/api/v1/notifications?unreadOnly=true&limit=50");
            Assert.Equal(HttpStatusCode.OK, unreadResponse.StatusCode);
            var unreadFeed = await unreadResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5, unreadFeed.GetProperty("unreadCount").GetInt32());
            var unreadItems = unreadFeed.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(5, unreadItems.Count);
            foreach (var item in unreadItems)
            {
                Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
            }

            // 3. Querying with before= oldest item of top 50 returns the remaining 5 notifications
            var oldestTop50CreatedAt = top50Items.Last().GetProperty("createdAt").GetDateTime();
            var oldestTop50Id = top50Items.Last().GetProperty("id").GetGuid();
            using var page2Response = await customer.GetAsync($"/api/v1/notifications?limit=50&before={oldestTop50CreatedAt:O}&beforeId={oldestTop50Id}");
            Assert.Equal(HttpStatusCode.OK, page2Response.StatusCode);
            var page2Feed = await page2Response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(page2Feed.GetProperty("hasMore").GetBoolean());
            var page2Items = page2Feed.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(5, page2Items.Count);
        }
    }

    [Fact]
    public async Task Notification_feed_cursor_paging_with_identical_timestamps_does_not_lose_items()
    {
        var (customer, customerId) = await CreateCustomer();
        using (customer)
        {
            await using var db = await fixture.OpenDatabaseAsync();
            var identicalTime = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
            for (var i = 1; i <= 52; i++)
            {
                var id = Guid.NewGuid();
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO store.in_app_notifications(id,recipient_id,event_key,notification_type,title,body,read_at,created_at,updated_at) " +
                    "VALUES (@id,@recipient,@event,'ORDER_UPDATE',@title,@body,NULL,@createdAt,@updatedAt)", db);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("recipient", customerId);
                insert.Parameters.AddWithValue("event", $"event_same_ts:{id:N}");
                insert.Parameters.AddWithValue("title", $"Thông báo cùng giờ #{i}");
                insert.Parameters.AddWithValue("body", $"Nội dung #{i}");
                insert.Parameters.AddWithValue("createdAt", identicalTime);
                insert.Parameters.AddWithValue("updatedAt", identicalTime);
                await insert.ExecuteNonQueryAsync();
            }

            // Page 1: 50 items with identical timestamp
            using var p1Response = await customer.GetAsync("/api/v1/notifications?limit=50");
            Assert.Equal(HttpStatusCode.OK, p1Response.StatusCode);
            var p1Feed = await p1Response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(p1Feed.GetProperty("hasMore").GetBoolean());
            var p1Items = p1Feed.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(50, p1Items.Count);

            // Page 2: with before and beforeId from last item of Page 1
            var lastP1 = p1Items.Last();
            var lastCreatedAt = lastP1.GetProperty("createdAt").GetDateTime();
            var lastId = lastP1.GetProperty("id").GetGuid();
            using var p2Response = await customer.GetAsync($"/api/v1/notifications?limit=50&before={lastCreatedAt:O}&beforeId={lastId}");
            Assert.Equal(HttpStatusCode.OK, p2Response.StatusCode);
            var p2Feed = await p2Response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(p2Feed.GetProperty("hasMore").GetBoolean());
            var p2Items = p2Feed.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, p2Items.Count);

            // No overlap between p1 and p2
            var p1Ids = p1Items.Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
            var p2Ids = p2Items.Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
            Assert.Empty(p1Ids.Intersect(p2Ids));
            Assert.Equal(52, p1Ids.Count + p2Ids.Count);
        }
    }

    [Fact]
    public async Task Notification_feed_with_partial_cursor_returns_400_invalid_cursor()
    {
        var (customer, _) = await CreateCustomer();
        using (customer)
        {
            // Only before
            using var resBeforeOnly = await customer.GetAsync($"/api/v1/notifications?before={DateTime.UtcNow:O}");
            Assert.Equal(HttpStatusCode.BadRequest, resBeforeOnly.StatusCode);
            var prob1 = await resBeforeOnly.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_CURSOR", prob1.GetProperty("code").GetString());

            // Only beforeId
            using var resIdOnly = await customer.GetAsync($"/api/v1/notifications?beforeId={Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.BadRequest, resIdOnly.StatusCode);
            var prob2 = await resIdOnly.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_CURSOR", prob2.GetProperty("code").GetString());
        }
    }
}
