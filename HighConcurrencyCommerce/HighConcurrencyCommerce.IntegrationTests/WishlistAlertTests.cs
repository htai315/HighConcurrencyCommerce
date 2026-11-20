using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Wishlist;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class WishlistAlertTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure alert pass 123!@#";

    [Fact]
    public async Task Alert_endpoints_enforce_RBAC_ownership_CSRF_and_no_store()
    {
        var (owner, ownerId) = await CreateCustomerAsync();
        var (other, _) = await CreateCustomerAsync();
        var product = await CreateProductAsync(ownerId, stock: 0);
        using (owner)
        using (other)
        {
            using var add = await SendAsync(owner, HttpMethod.Put, $"wishlist/items/{product.ProductId}");
            Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

            using var guest = fixture.CreateClient();
            using var guestGet = await guest.GetAsync($"/api/v1/wishlist/items/{product.ProductId}/alerts");
            Assert.Equal(HttpStatusCode.Unauthorized, guestGet.StatusCode);

            foreach (var role in new[] { "STAFF", "ADMIN" })
            {
                using var roleClient = await CreateRoleClientAsync(role);
                using var forbidden = await roleClient.GetAsync($"/api/v1/wishlist/items/{product.ProductId}/alerts");
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            using var crossOwner = await other.GetAsync($"/api/v1/wishlist/items/{product.ProductId}/alerts");
            Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
            Assert.Equal("WISHLIST_ITEM_NOT_FOUND", await ProblemCodeAsync(crossOwner));

            using var csrfMissingRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/wishlist/items/{product.ProductId}/alerts")
            {
                Content = JsonContent.Create(new UpdateWishlistAlertSettingsRequest
                {
                    BackInStockEnabled = true,
                    ExpectedRevision = 0
                })
            };
            using var csrfMissing = await owner.SendAsync(csrfMissingRequest);
            Assert.Equal(HttpStatusCode.BadRequest, csrfMissing.StatusCode);
            Assert.Equal("CSRF_INVALID", await ProblemCodeAsync(csrfMissing));

            using var get = await owner.GetAsync($"/api/v1/wishlist/items/{product.ProductId}/alerts");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.True(get.Headers.CacheControl?.NoStore);

            using var invalidRevision = await SendAsync(
                owner,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = true, priceDropEnabled = false, emailEnabled = false, expectedRevision = -1 });
            Assert.Equal(HttpStatusCode.BadRequest, invalidRevision.StatusCode);
            Assert.Equal("VALIDATION_ERROR", await ProblemCodeAsync(invalidRevision));
        }
    }

    [Fact]
    public async Task Enabling_alerts_snapshots_current_state_and_replay_is_idempotent()
    {
        var (customer, customerId) = await CreateCustomerAsync();
        var product = await CreateProductAsync(customerId, price: 1_800_000m, stock: 0);
        using (customer)
        {
            using var add = await SendAsync(customer, HttpMethod.Put, $"wishlist/items/{product.ProductId}");
            Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

            var request = new UpdateWishlistAlertSettingsRequest
            {
                BackInStockEnabled = true,
                PriceDropEnabled = true,
                EmailEnabled = true,
                ExpectedRevision = 0
            };
            using var enabled = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                request);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.True(enabled.Headers.CacheControl?.NoStore);
            var dto = await enabled.Content.ReadFromJsonAsync<WishlistAlertSettingsDto>();
            Assert.NotNull(dto);
            Assert.True(dto.BackInStockEnabled);
            Assert.True(dto.PriceDropEnabled);
            Assert.True(dto.EmailEnabled);
            Assert.Equal(WishlistAvailability.OutOfStock, dto.CurrentAvailability);
            Assert.Equal(1_800_000m, dto.CurrentMinPrice);
            Assert.Equal(1, dto.Revision);

            // Simulate a lost response: replay the same payload with the previous revision.
            using var replay = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(1, (await replay.Content.ReadFromJsonAsync<WishlistAlertSettingsDto>())!.Revision);

            await using var db = await fixture.OpenDatabaseAsync();
            await using var snapshot = new NpgsqlCommand("""
                SELECT last_observed_in_stock, last_observed_min_price, alert_revision,
                       last_evaluated_at IS NOT NULL
                FROM store.customer_wishlist_items
                WHERE customer_id=@customerId AND product_id=@productId
                """, db);
            snapshot.Parameters.AddWithValue("customerId", customerId);
            snapshot.Parameters.AddWithValue("productId", product.ProductId);
            await using var reader = await snapshot.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.Equal(1_800_000m, reader.GetDecimal(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.True(reader.GetBoolean(3));
            await reader.CloseAsync();

            var summary = await customer.GetFromJsonAsync<WishlistSummaryDto>("/api/v1/wishlist/summary");
            var summaryAlert = Assert.Single(summary!.Alerts);
            Assert.Equal(product.ProductId, summaryAlert.ProductId);
            Assert.True(summaryAlert.BackInStockEnabled);
            Assert.Equal(1, summaryAlert.Revision);

            var page = await customer.GetFromJsonAsync<WishlistPageDto>("/api/v1/wishlist");
            var item = Assert.Single(page!.Items);
            Assert.True(item.Alerts.PriceDropEnabled);
            Assert.True(item.Alerts.EmailEnabled);
        }
    }

    [Fact]
    public async Task Email_requires_verified_account_and_invalid_channel_combination_is_rejected()
    {
        var (customer, customerId) = await CreateCustomerAsync();
        var product = await CreateProductAsync(customerId);
        using (customer)
        {
            using var add = await SendAsync(customer, HttpMethod.Put, $"wishlist/items/{product.ProductId}");
            Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var unverify = new NpgsqlCommand(
                "UPDATE store.user_accounts SET email_verified_at=NULL WHERE id=@id", db))
            {
                unverify.Parameters.AddWithValue("id", customerId);
                await unverify.ExecuteNonQueryAsync();
            }

            using var unverified = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = true, priceDropEnabled = false, emailEnabled = true, expectedRevision = 0 });
            Assert.Equal(HttpStatusCode.BadRequest, unverified.StatusCode);
            Assert.Equal("EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL", await ProblemCodeAsync(unverified));

            using var invalidChannels = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = false, priceDropEnabled = false, emailEnabled = true, expectedRevision = 0 });
            Assert.Equal(HttpStatusCode.BadRequest, invalidChannels.StatusCode);
            Assert.Equal("INVALID_ALERT_SETTINGS", await ProblemCodeAsync(invalidChannels));

            using var inAppOnly = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = true, priceDropEnabled = false, emailEnabled = false, expectedRevision = 0 });
            Assert.Equal(HttpStatusCode.OK, inAppOnly.StatusCode);
            Assert.Equal(1, (await inAppOnly.Content.ReadFromJsonAsync<WishlistAlertSettingsDto>())!.Revision);
        }
    }

    [Fact]
    public async Task Product_unavailable_blocks_new_alerts_but_existing_alerts_can_be_disabled()
    {
        var (customer, customerId) = await CreateCustomerAsync();
        var product = await CreateProductAsync(customerId, stock: 3);
        using (customer)
        {
            using var add = await SendAsync(customer, HttpMethod.Put, $"wishlist/items/{product.ProductId}");
            Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
            using var enable = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = true, priceDropEnabled = false, emailEnabled = false, expectedRevision = 0 });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

            await using (var db = await fixture.OpenDatabaseAsync())
            await using (var suspend = new NpgsqlCommand(
                "UPDATE store.products SET publication_status='SUSPENDED' WHERE id=@id", db))
            {
                suspend.Parameters.AddWithValue("id", product.ProductId);
                await suspend.ExecuteNonQueryAsync();
            }

            using var get = await customer.GetAsync($"/api/v1/wishlist/items/{product.ProductId}/alerts");
            var current = await get.Content.ReadFromJsonAsync<WishlistAlertSettingsDto>();
            Assert.Equal(WishlistAvailability.ProductUnavailable, current!.CurrentAvailability);
            Assert.Null(current.CurrentMinPrice);

            using var enableAnother = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = true, priceDropEnabled = true, emailEnabled = false, expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.Conflict, enableAnother.StatusCode);
            Assert.Equal("WISHLIST_ALERT_PRODUCT_UNAVAILABLE", await ProblemCodeAsync(enableAnother));

            using var disable = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = false, priceDropEnabled = false, emailEnabled = false, expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            var disabled = await disable.Content.ReadFromJsonAsync<WishlistAlertSettingsDto>();
            Assert.False(disabled!.BackInStockEnabled);
            Assert.Equal(2, disabled.Revision);

            await using var verify = await fixture.OpenDatabaseAsync();
            await using var observed = new NpgsqlCommand("""
                SELECT last_observed_in_stock IS NULL AND last_observed_min_price IS NULL
                FROM store.customer_wishlist_items
                WHERE customer_id=@customerId AND product_id=@productId
                """, verify);
            observed.Parameters.AddWithValue("customerId", customerId);
            observed.Parameters.AddWithValue("productId", product.ProductId);
            Assert.True((bool)(await observed.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Concurrent_updates_allow_one_winner_and_stale_response_exposes_current_revision()
    {
        var (customer, customerId) = await CreateCustomerAsync();
        var product = await CreateProductAsync(customerId, stock: 1);
        using (customer)
        {
            using var add = await SendAsync(customer, HttpMethod.Put, $"wishlist/items/{product.ProductId}");
            Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string> UpdateAsync(bool stock, bool price)
            {
                using var scope = fixture.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IWishlistAlertService>();
                await gate.Task;
                try
                {
                    await service.UpdateSettingsAsync(customerId, product.ProductId, new UpdateWishlistAlertSettingsRequest
                    {
                        BackInStockEnabled = stock,
                        PriceDropEnabled = price,
                        ExpectedRevision = 0
                    });
                    return "OK";
                }
                catch (WishlistFailure failure)
                {
                    Assert.Equal(1, failure.CurrentRevision);
                    return failure.Code;
                }
            }

            var first = UpdateAsync(stock: true, price: false);
            var second = UpdateAsync(stock: false, price: true);
            gate.SetResult();
            var outcomes = await Task.WhenAll(first, second);
            Assert.Equal(1, outcomes.Count(x => x == "OK"));
            Assert.Equal(1, outcomes.Count(x => x == "STALE_WISHLIST_ALERT_SETTINGS"));

            await using var db = await fixture.OpenDatabaseAsync();
            await using var stateCommand = new NpgsqlCommand("""
                SELECT back_in_stock_enabled, price_drop_enabled, alert_revision
                FROM store.customer_wishlist_items
                WHERE customer_id=@customerId AND product_id=@productId
                """, db);
            stateCommand.Parameters.AddWithValue("customerId", customerId);
            stateCommand.Parameters.AddWithValue("productId", product.ProductId);
            await using var reader = await stateCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var backEnabled = reader.GetBoolean(0);
            var priceEnabled = reader.GetBoolean(1);
            Assert.NotEqual(backEnabled, priceEnabled);
            Assert.Equal(1, reader.GetInt32(2));
            await reader.CloseAsync();

            using var stale = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new { backInStockEnabled = true, priceDropEnabled = true, emailEnabled = false, expectedRevision = 0 });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("STALE_WISHLIST_ALERT_SETTINGS", problem.GetProperty("code").GetString());
            Assert.Equal(1, problem.GetProperty("currentRevision").GetInt32());

            // Replay the winning payload with old revision: no second write.
            using var replay = await SendAsync(
                customer,
                HttpMethod.Put,
                $"wishlist/items/{product.ProductId}/alerts",
                new
                {
                    backInStockEnabled = backEnabled,
                    priceDropEnabled = priceEnabled,
                    emailEnabled = false,
                    expectedRevision = 0
                });
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(1, (await replay.Content.ReadFromJsonAsync<WishlistAlertSettingsDto>())!.Revision);
        }
    }

    [Fact]
    public async Task Concurrent_remove_and_alert_update_are_serialized_without_deadlock_or_lost_delete()
    {
        var customerData = await CreateCustomerAsync();
        using var customer = customerData.Client;
        var customerId = customerData.UserId;
        var product = await CreateProductAsync(customerId, stock: 0);
        using (var addScope = fixture.Services.CreateScope())
        {
            await addScope.ServiceProvider.GetRequiredService<IWishlistService>()
                .AddItemAsync(customerId, product.ProductId);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> UpdateAsync()
        {
            using var scope = fixture.Services.CreateScope();
            await gate.Task;
            try
            {
                await scope.ServiceProvider.GetRequiredService<IWishlistAlertService>().UpdateSettingsAsync(
                    customerId,
                    product.ProductId,
                    new UpdateWishlistAlertSettingsRequest { BackInStockEnabled = true, ExpectedRevision = 0 });
                return "UPDATED";
            }
            catch (WishlistFailure failure)
            {
                Assert.Equal("WISHLIST_ITEM_NOT_FOUND", failure.Code);
                return "NOT_FOUND";
            }
        }

        async Task RemoveAsync()
        {
            using var scope = fixture.Services.CreateScope();
            await gate.Task;
            await scope.ServiceProvider.GetRequiredService<IWishlistService>()
                .RemoveItemAsync(customerId, product.ProductId);
        }

        var update = UpdateAsync();
        var remove = RemoveAsync();
        gate.SetResult();
        var outcome = await update;
        await remove;

        Assert.Contains(outcome, new[] { "UPDATED", "NOT_FOUND" });
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS(
                SELECT 1 FROM store.customer_wishlist_items
                WHERE customer_id=@customerId AND product_id=@productId)
            """, db);
        command.Parameters.AddWithValue("customerId", customerId);
        command.Parameters.AddWithValue("productId", product.ProductId);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateCustomerAsync()
    {
        var client = fixture.CreateClient();
        var email = $"wishlist_alert_{Guid.NewGuid():N}@example.invalid";
        using var register = await SendAsync(client, HttpMethod.Post, "auth/register", new
        {
            email,
            password = Password,
            confirmPassword = Password,
            fullName = "Wishlist Alert Customer"
        });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await fixture.VerifyUserAsync(email);
        using var login = await SendAsync(client, HttpMethod.Post, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var idCommand = new NpgsqlCommand(
            "SELECT id FROM store.user_accounts WHERE lower(email)=lower(@email)", db);
        idCommand.Parameters.AddWithValue("email", email);
        return (client, (Guid)(await idCommand.ExecuteScalarAsync())!);
    }

    private async Task<HttpClient> CreateRoleClientAsync(string role)
    {
        var id = Guid.NewGuid();
        var email = $"wishlist_alert_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@example.invalid";
        var hash = new PasswordHasher<object>().HashPassword(new object(), Password);
        await using var db = await fixture.OpenDatabaseAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO store.user_accounts(
                id, role_id, email, password_hash, full_name, account_status, email_verified_at)
            VALUES(@id, (SELECT id FROM store.roles WHERE role_key=@role), @email, @hash,
                   @name, 'ACTIVE', now())
            """, db);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("role", role);
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("name", $"{role} Alert User");
        await insert.ExecuteNonQueryAsync();

        var client = fixture.CreateClient();
        using var login = await SendAsync(client, HttpMethod.Post, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<(Guid ProductId, Guid SkuId)> CreateProductAsync(
        Guid actorId,
        decimal price = 2_000_000m,
        int stock = 1)
    {
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO store.products(
                id, title, publication_status, created_by, published_by, published_at)
            VALUES(@productId, 'Wishlist Alert Product', 'PUBLISHED', @actorId, @actorId, now());
            INSERT INTO store.product_skus(
                id, product_id, sku_code, option_label, regular_price, sku_status)
            VALUES(@skuId, @productId, @skuCode, '4U/G5', @price, 'ACTIVE');
            INSERT INTO store.inventory_balances(
                id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty)
            VALUES(gen_random_uuid(), @skuId, @stock, @stock, 0, 0);
            """, db);
        command.Parameters.AddWithValue("productId", productId);
        command.Parameters.AddWithValue("skuId", skuId);
        command.Parameters.AddWithValue("skuCode", $"ALERT-{skuId:N}");
        command.Parameters.AddWithValue("actorId", actorId);
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("stock", stock);
        await command.ExecuteNonQueryAsync();
        return (productId, skuId);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string relativePath,
        object? body = null)
    {
        using var csrf = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        var csrfBody = await csrf.Content.ReadFromJsonAsync<JsonElement>();
        using var request = new HttpRequestMessage(method, "/api/v1/" + relativePath);
        request.Headers.Add("X-CSRF-TOKEN", csrfBody.GetProperty("requestToken").GetString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
