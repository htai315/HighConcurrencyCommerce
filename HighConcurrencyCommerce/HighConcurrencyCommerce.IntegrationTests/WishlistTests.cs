using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Wishlist;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Wishlist;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class WishlistTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure test pass 123!@#";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateAuthenticatedCustomerClient()
    {
        var client = fixture.CreateClient();
        var email = $"wishlist_cust_{Guid.NewGuid():N}@example.invalid";
        using var reg = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Wishlist Customer" });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);
        await fixture.VerifyUserAsync(email);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM store.user_accounts WHERE lower(email) = lower(@email)", db);
        cmd.Parameters.AddWithValue("email", email);
        var id = (Guid)(await cmd.ExecuteScalarAsync())!;
        return (client, id);
    }

    private async Task<(HttpClient Client, Guid UserId)> CreateAuthenticatedRoleClient(string roleKey)
    {
        var id = Guid.NewGuid();
        var email = $"wishlist_{roleKey.ToLowerInvariant()}_{Guid.NewGuid():N}@example.invalid";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @roleKey", db);
        roleCmd.Parameters.AddWithValue("roleKey", roleKey);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
        var hash = hasher.HashPassword(new object(), Password);
        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status, email_verified_at) " +
            "VALUES (@id, @roleId, @email, @hash, @name, 'ACTIVE', now())", db);
        insertCmd.Parameters.AddWithValue("id", id);
        insertCmd.Parameters.AddWithValue("roleId", roleId);
        insertCmd.Parameters.AddWithValue("email", email);
        insertCmd.Parameters.AddWithValue("hash", hash);
        insertCmd.Parameters.AddWithValue("name", $"{roleKey} User");
        await insertCmd.ExecuteNonQueryAsync();

        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, id);
    }

    private async Task<Guid> EnsureActorAsync(NpgsqlConnection db)
    {
        await using var cmd = new NpgsqlCommand("SELECT id FROM store.user_accounts LIMIT 1", db);
        var existing = await cmd.ExecuteScalarAsync();
        if (existing is Guid g) return g;

        var actorId = Guid.NewGuid();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = 'ADMIN'", db);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
        var hash = hasher.HashPassword(new object(), Password);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status, email_verified_at) " +
            "VALUES (@id, @roleId, @email, @hash, 'Seed Admin', 'ACTIVE', now())", db);
        insert.Parameters.AddWithValue("id", actorId);
        insert.Parameters.AddWithValue("roleId", roleId);
        insert.Parameters.AddWithValue("email", $"actor_{Guid.NewGuid():N}@example.invalid");
        insert.Parameters.AddWithValue("hash", hash);
        await insert.ExecuteNonQueryAsync();
        return actorId;
    }

    private async Task<Guid> CreateProductAsync(string title = "Vợt Cầu Lông Wishlist Test", string publicationStatus = "PUBLISHED")
    {
        var id = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        var actorId = await EnsureActorAsync(db);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO store.products (id, title, publication_status, created_by, published_by, published_at) " +
            "VALUES (@id, @title, @status, @actorId, @actorId, now())", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("status", publicationStatus);
        cmd.Parameters.AddWithValue("actorId", actorId);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> CreateOrderItemAsync(Guid customerId, Guid skuId)
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var orderNumber = $"ORD-{Guid.NewGuid():N}"[..20];

        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO store.sales_orders (id, customer_id, order_number, order_status, payment_method, shipping_address_snapshot, total_amount, subtotal_amount, voucher_discount_amount, deadline_at) " +
            "VALUES (@orderId, @customerId, @orderNumber, 'COMPLETED', 'COD', '{\"recipientName\": \"Test\", \"phone\": \"0901234567\", \"addressLine\": \"123 Str\", \"province\": \"HCM\"}'::jsonb, 1000000, 1000000, 0, now() + interval '1 day'); " +
            "INSERT INTO store.sales_order_items (id, order_id, product_sku_id, quantity, unit_price, product_title_snapshot, sku_code_snapshot, sku_option_label_snapshot) " +
            "VALUES (@itemId, @orderId, @skuId, 1, 1000000, 'Snapshot Product Title', 'SKU-SNAP', '4U');", db);
        cmd.Parameters.AddWithValue("orderId", orderId);
        cmd.Parameters.AddWithValue("customerId", customerId);
        cmd.Parameters.AddWithValue("orderNumber", orderNumber);
        cmd.Parameters.AddWithValue("itemId", itemId);
        cmd.Parameters.AddWithValue("skuId", skuId);
        await cmd.ExecuteNonQueryAsync();

        return itemId;
    }

    private async Task<Guid> CreateSkuAsync(Guid productId, decimal price = 2500000m, int stockQty = 20, string skuStatus = "ACTIVE")
    {
        var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO store.product_skus (id, product_id, sku_code, option_label, regular_price, sku_status) " +
            "VALUES (@id, @productId, @code, '4U5', @price, @skuStatus)", db);
        cmd.Parameters.AddWithValue("id", skuId);
        cmd.Parameters.AddWithValue("productId", productId);
        cmd.Parameters.AddWithValue("code", $"SKU-{skuId.ToString("N")[..8]}");
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("skuStatus", skuStatus);
        await cmd.ExecuteNonQueryAsync();

        await using var stockCmd = new NpgsqlCommand(
            "INSERT INTO store.inventory_balances (id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, revision) " +
            "VALUES (gen_random_uuid(), @skuId, @qty, @qty, 0, 0, 0)", db);
        stockCmd.Parameters.AddWithValue("skuId", skuId);
        stockCmd.Parameters.AddWithValue("qty", stockQty);
        await stockCmd.ExecuteNonQueryAsync();

        return skuId;
    }

    // =========================================================================
    // 1. RBAC and CSRF
    // =========================================================================
    [Fact]
    public async Task RBAC_WishlistEndpoints_EnforcesPoliciesAndCsrf()
    {
        var productId = await CreateProductAsync();
        await CreateSkuAsync(productId);
        using var guest = fixture.CreateClient();

        // 1. Guest -> 401 Unauthorized
        var guestGet = await guest.GetAsync("/api/v1/wishlist");
        Assert.Equal(HttpStatusCode.Unauthorized, guestGet.StatusCode);

        var guestSummary = await guest.GetAsync("/api/v1/wishlist/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, guestSummary.StatusCode);

        var guestPut = await guest.PutAsync($"/api/v1/wishlist/items/{productId}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, guestPut.StatusCode);

        var guestDel = await guest.DeleteAsync($"/api/v1/wishlist/items/{productId}");
        Assert.Equal(HttpStatusCode.Unauthorized, guestDel.StatusCode);

        // 2. Staff -> 403 Forbidden
        var (staffClient, _) = await CreateAuthenticatedRoleClient("STAFF");
        using (staffClient)
        {
            var staffGet = await staffClient.GetAsync("/api/v1/wishlist");
            Assert.Equal(HttpStatusCode.Forbidden, staffGet.StatusCode);

            var staffPut = await Send(staffClient, $"wishlist/items/{productId}", method: HttpMethod.Put);
            Assert.Equal(HttpStatusCode.Forbidden, staffPut.StatusCode);
        }

        // 3. Admin -> 403 Forbidden
        var (adminClient, _) = await CreateAuthenticatedRoleClient("ADMIN");
        using (adminClient)
        {
            var adminGet = await adminClient.GetAsync("/api/v1/wishlist");
            Assert.Equal(HttpStatusCode.Forbidden, adminGet.StatusCode);

            var adminPut = await Send(adminClient, $"wishlist/items/{productId}", method: HttpMethod.Put);
            Assert.Equal(HttpStatusCode.Forbidden, adminPut.StatusCode);
        }

        // 4. Customer without CSRF -> 400 BadRequest (CsrfFilter)
        var (custClient, _) = await CreateAuthenticatedCustomerClient();
        using (custClient)
        {
            using var noCsrfPut = await custClient.PutAsync($"/api/v1/wishlist/items/{productId}", null);
            Assert.Equal(HttpStatusCode.BadRequest, noCsrfPut.StatusCode);
            var noCsrfBody = await noCsrfPut.Content.ReadAsStringAsync();
            Assert.Contains("CSRF_INVALID", noCsrfBody);

            using var withCsrfPut = await Send(custClient, $"wishlist/items/{productId}", method: HttpMethod.Put);
            Assert.Equal(HttpStatusCode.NoContent, withCsrfPut.StatusCode);
        }
    }

    // =========================================================================
    // 2. Customer Isolation
    // =========================================================================
    [Fact]
    public async Task CustomerIsolation_WishlistsAreIndependent()
    {
        var (custA, idA) = await CreateAuthenticatedCustomerClient();
        var (custB, idB) = await CreateAuthenticatedCustomerClient();
        using (custA)
        using (custB)
        {
            var product1 = await CreateProductAsync("Vợt Chung 1");
            await CreateSkuAsync(product1);
            var product2 = await CreateProductAsync("Vợt Chung 2");
            await CreateSkuAsync(product2);

            // A adds product 1 and 2
            Assert.Equal(HttpStatusCode.NoContent, (await Send(custA, $"wishlist/items/{product1}", method: HttpMethod.Put)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Send(custA, $"wishlist/items/{product2}", method: HttpMethod.Put)).StatusCode);

            // B adds product 1
            Assert.Equal(HttpStatusCode.NoContent, (await Send(custB, $"wishlist/items/{product1}", method: HttpMethod.Put)).StatusCode);

            // B deletes product 1
            Assert.Equal(HttpStatusCode.NoContent, (await Send(custB, $"wishlist/items/{product1}", method: HttpMethod.Delete)).StatusCode);

            // Verify B's summary is empty
            var sumB = await custB.GetFromJsonAsync<WishlistSummaryDto>("/api/v1/wishlist/summary");
            Assert.NotNull(sumB);
            Assert.Equal(0, sumB.TotalCount);
            Assert.Empty(sumB.ProductIds);

            // Verify A STILL has both product 1 and product 2
            var sumA = await custA.GetFromJsonAsync<WishlistSummaryDto>("/api/v1/wishlist/summary");
            Assert.NotNull(sumA);
            Assert.Equal(2, sumA.TotalCount);
            Assert.Contains(product1, sumA.ProductIds);
            Assert.Contains(product2, sumA.ProductIds);

            var pageA = await custA.GetFromJsonAsync<WishlistPageDto>("/api/v1/wishlist");
            Assert.NotNull(pageA);
            Assert.Equal(2, pageA.TotalCount);
            Assert.Equal(2, pageA.Items.Count);
        }
    }

    // =========================================================================
    // 3. Add, Remove & Validation
    // =========================================================================
    [Fact]
    public async Task AddItem_PublicInStockAndOutOfStock_SucceedsIdempotently()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            using var scope = fixture.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();

            // 1. In stock product
            var inStockProd = await CreateProductAsync("Vợt Còn Hàng");
            await CreateSkuAsync(inStockProd, stockQty: 10);
            await service.AddItemAsync(custId, inStockProd);

            // 2. Out of stock product
            var outOfStockProd = await CreateProductAsync("Vợt Hết Hàng");
            await CreateSkuAsync(outOfStockProd, stockQty: 0);
            await service.AddItemAsync(custId, outOfStockProd);

            // 3. Verify availability status in list
            var page = await service.GetWishlistAsync(custId);
            Assert.Equal(2, page.TotalCount);

            var inStockItem = page.Items.Single(i => i.ProductId == inStockProd);
            Assert.Equal(WishlistAvailability.Available, inStockItem.Availability);
            Assert.NotNull(inStockItem.Product);
            Assert.True(inStockItem.Product.InStock);

            var outOfStockItem = page.Items.Single(i => i.ProductId == outOfStockProd);
            Assert.Equal(WishlistAvailability.OutOfStock, outOfStockItem.Availability);
            Assert.NotNull(outOfStockItem.Product);
            Assert.False(outOfStockItem.Product.InStock);

            // 4. PUT missing product -> 404
            var exMissing = await Assert.ThrowsAsync<WishlistFailure>(() => service.AddItemAsync(custId, Guid.NewGuid()));
            Assert.Equal("PRODUCT_NOT_FOUND", exMissing.Code);
            Assert.Equal(404, exMissing.Status);

            // 5. PUT DRAFT product -> 404
            var draftProd = await CreateProductAsync("Vợt Draft", publicationStatus: "DRAFT");
            await CreateSkuAsync(draftProd);
            var exDraft = await Assert.ThrowsAsync<WishlistFailure>(() => service.AddItemAsync(custId, draftProd));
            Assert.Equal("PRODUCT_NOT_FOUND", exDraft.Code);

            // 6. PUT ARCHIVED product -> 404
            var archivedProd = await CreateProductAsync("Vợt Archived", publicationStatus: "ARCHIVED");
            await CreateSkuAsync(archivedProd);
            var exArchived = await Assert.ThrowsAsync<WishlistFailure>(() => service.AddItemAsync(custId, archivedProd));
            Assert.Equal("PRODUCT_NOT_FOUND", exArchived.Code);

            // 7. PUT product with no active SKUs -> 404
            var noSkuProd = await CreateProductAsync("Vợt No SKU");
            var exNoSku = await Assert.ThrowsAsync<WishlistFailure>(() => service.AddItemAsync(custId, noSkuProd));
            Assert.Equal("PRODUCT_NOT_FOUND", exNoSku.Code);

            // 8. Repeated PUT on already-saved item -> no-op success, created_at unchanged
            var beforeAddedAt = inStockItem.AddedAt;
            await Task.Delay(10);
            await service.AddItemAsync(custId, inStockProd);
            var pageAfter = await service.GetWishlistAsync(custId);
            var inStockAfter = pageAfter.Items.Single(i => i.ProductId == inStockProd);
            Assert.Equal(beforeAddedAt, inStockAfter.AddedAt);

            // 9. Repeated DELETE on non-existent or existing item -> 204
            await service.RemoveItemAsync(custId, inStockProd);
            await service.RemoveItemAsync(custId, inStockProd); // second delete is no-op
            await service.RemoveItemAsync(custId, Guid.NewGuid()); // random id delete is no-op
        }
    }

    // =========================================================================
    // 4. Concurrency: Same Product
    // =========================================================================
    [Fact]
    public async Task Concurrent_PUT_SameProduct_MaintainsSingleRow()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            var product = await CreateProductAsync("Vợt Concurrency Race");
            await CreateSkuAsync(product);

            // Fire 10 concurrent requests
            var tasks = Enumerable.Range(0, 10).Select(async _ =>
            {
                using var scope = fixture.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();
                await service.AddItemAsync(custId, product);
            });

            await Task.WhenAll(tasks);

            // Exactly 1 row in DB
            await using var db = await fixture.OpenDatabaseAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM store.customer_wishlist_items WHERE customer_id = @c AND product_id = @p", db);
            cmd.Parameters.AddWithValue("c", custId);
            cmd.Parameters.AddWithValue("p", product);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1, count);
        }
    }

    // =========================================================================
    // 5. Wishlist Cap (500 items) and Boundary Concurrency
    // =========================================================================
    [Fact]
    public async Task WishlistLimit_500Items_EnforcedUnderConcurrency()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            using var scope = fixture.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();

            // Create 500 dummy products directly in DB
            var productIds = new List<Guid>();
            await using var db = await fixture.OpenDatabaseAsync();
            var actorId = await EnsureActorAsync(db);
            for (var i = 0; i < 500; i++)
            {
                var pid = Guid.NewGuid();
                productIds.Add(pid);
                await using var pcmd = new NpgsqlCommand(
                    "INSERT INTO store.products (id, title, publication_status, created_by, published_by, published_at) " +
                    "VALUES (@id, @title, 'PUBLISHED', @actorId, @actorId, now())", db);
                pcmd.Parameters.AddWithValue("id", pid);
                pcmd.Parameters.AddWithValue("title", $"Bulk Product {i}");
                pcmd.Parameters.AddWithValue("actorId", actorId);
                await pcmd.ExecuteNonQueryAsync();

                var skuid = Guid.NewGuid();
                await using var scmd = new NpgsqlCommand(
                    "INSERT INTO store.product_skus (id, product_id, sku_code, option_label, regular_price, sku_status) " +
                    "VALUES (@id, @pid, @code, '4U5', 1000000, 'ACTIVE')", db);
                scmd.Parameters.AddWithValue("id", skuid);
                scmd.Parameters.AddWithValue("pid", pid);
                scmd.Parameters.AddWithValue("code", $"BULK-{skuid.ToString("N")[..8]}");
                await scmd.ExecuteNonQueryAsync();

                if (i < 499)
                {
                    // Add first 499 directly into customer wishlist
                    await using var wcmd = new NpgsqlCommand(
                        "INSERT INTO store.customer_wishlist_items (customer_id, product_id, created_at) " +
                        "VALUES (@cid, @pid, now())", db);
                    wcmd.Parameters.AddWithValue("cid", custId);
                    wcmd.Parameters.AddWithValue("pid", pid);
                    await wcmd.ExecuteNonQueryAsync();
                }
            }

            // Wishlist currently has 499 items. Race two different products for the final slot.
            var p500 = productIds[499];
            var p501 = await CreateProductAsync("Product 501");
            await CreateSkuAsync(p501);

            static async Task<string> TryAddAsync(CatalogFixture fixture, Guid customerId, Guid productId)
            {
                using var raceScope = fixture.Services.CreateScope();
                var raceService = raceScope.ServiceProvider.GetRequiredService<IWishlistService>();
                try
                {
                    await raceService.AddItemAsync(customerId, productId);
                    return "OK";
                }
                catch (WishlistFailure failure)
                {
                    return failure.Code;
                }
            }

            var race = await Task.WhenAll(
                TryAddAsync(fixture, custId, p500),
                TryAddAsync(fixture, custId, p501));

            Assert.Equal(1, race.Count(result => result == "OK"));
            Assert.Equal(1, race.Count(result => result == "WISHLIST_LIMIT_REACHED"));

            var summary = await service.GetSummaryAsync(custId);
            Assert.Equal(500, summary.TotalCount);
            var acceptedProduct = summary.ProductIds.Contains(p500) ? p500 : p501;
            var rejectedProduct = acceptedProduct == p500 ? p501 : p500;

            // The rejected contender remains blocked while the list is full.
            var exLimit = await Assert.ThrowsAsync<WishlistFailure>(() => service.AddItemAsync(custId, rejectedProduct));
            Assert.Equal("WISHLIST_LIMIT_REACHED", exLimit.Code);
            Assert.Equal(409, exLimit.Status);

            // While full at 500, PUTting an ALREADY-SAVED item succeeds (no-op 204).
            await service.AddItemAsync(custId, acceptedProduct);

            // Remove the winner -> now 499 -> adding the rejected contender succeeds.
            await service.RemoveItemAsync(custId, acceptedProduct);
            Assert.Equal(499, (await service.GetSummaryAsync(custId)).TotalCount);

            await service.AddItemAsync(custId, rejectedProduct);
            Assert.Equal(500, (await service.GetSummaryAsync(custId)).TotalCount);
        }
    }

    // =========================================================================
    // 6. Product Transitions & Placeholders
    // =========================================================================
    [Fact]
    public async Task ProductTransitions_UnlistedProductBecomesPlaceholder()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            using var scope = fixture.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();

            var product = await CreateProductAsync("Vợt Sắp Bị Archived");
            await CreateSkuAsync(product);

            await service.AddItemAsync(custId, product);

            // Transition product to ARCHIVED in DB
            await using var db = await fixture.OpenDatabaseAsync();
            await using var cmd = new NpgsqlCommand("UPDATE store.products SET publication_status = 'ARCHIVED' WHERE id = @id", db);
            cmd.Parameters.AddWithValue("id", product);
            await cmd.ExecuteNonQueryAsync();

            // Wishlist page reflects placeholder
            var page = await service.GetWishlistAsync(custId);
            Assert.Equal(1, page.TotalCount);
            var item = Assert.Single(page.Items);
            Assert.Equal(product, item.ProductId);
            Assert.Equal(WishlistAvailability.ProductUnavailable, item.Availability);
            Assert.Null(item.Product); // Leaks no metadata

            // Summary still includes the ID
            var summary = await service.GetSummaryAsync(custId);
            Assert.Equal(1, summary.TotalCount);
            Assert.Contains(product, summary.ProductIds);

            // Customer can still DELETE the unlisted item
            await service.RemoveItemAsync(custId, product);
            Assert.Equal(0, (await service.GetSummaryAsync(custId)).TotalCount);
        }
    }

    // =========================================================================
    // 7. Dynamic Updates (Price, Stock, Reviews)
    // =========================================================================
    [Fact]
    public async Task DynamicUpdates_PriceStockRatingReflectedOnSubsequentGet()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            using var scope = fixture.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();

            var product = await CreateProductAsync("Vợt Dynamic Test");
            var skuId = await CreateSkuAsync(product, price: 3000000m, stockQty: 5);

            await service.AddItemAsync(custId, product);

            // Initial state
            var page1 = await service.GetWishlistAsync(custId);
            var item1 = Assert.Single(page1.Items);
            Assert.Equal(3000000m, item1.Product!.MinPrice);
            Assert.True(item1.Product.InStock);
            Assert.Equal(0.00m, item1.Product.AverageRating);
            Assert.Equal(0, item1.Product.ReviewCount);

            // Update inventory to OUT OF STOCK (availableQty = 0)
            await using var db = await fixture.OpenDatabaseAsync();
            await using var stockCmd = new NpgsqlCommand("UPDATE store.inventory_balances SET available_qty = 0, allocated_qty = physical_qty WHERE product_sku_id = @skuId", db);
            stockCmd.Parameters.AddWithValue("skuId", skuId);
            await stockCmd.ExecuteNonQueryAsync();

            var orderItemId = await CreateOrderItemAsync(custId, skuId);

            // Insert a review for product
            var reviewId = Guid.NewGuid();
            await using var revCmd = new NpgsqlCommand(
                "INSERT INTO store.product_reviews (id, customer_id, product_id, qualifying_order_item_id, rating, content, review_status) " +
                "VALUES (@id, @cid, @pid, @orderItemId, 5, 'Vợt quá tuyệt vời chuẩn 5 sao!', 'PUBLISHED')", db);
            revCmd.Parameters.AddWithValue("id", reviewId);
            revCmd.Parameters.AddWithValue("cid", custId);
            revCmd.Parameters.AddWithValue("pid", product);
            revCmd.Parameters.AddWithValue("orderItemId", orderItemId);
            await revCmd.ExecuteNonQueryAsync();

            // Next GET reflects updated stock and rating
            var page2 = await service.GetWishlistAsync(custId);
            var item2 = Assert.Single(page2.Items);
            Assert.Equal(WishlistAvailability.OutOfStock, item2.Availability);
            Assert.False(item2.Product!.InStock);
            Assert.Equal(5.00m, item2.Product.AverageRating);
            Assert.Equal(1, item2.Product.ReviewCount);
        }
    }

    // =========================================================================
    // 8. Pagination & Sorting
    // =========================================================================
    [Fact]
    public async Task PaginationAndSorting_ReturnsStableOrderedPagesAndValidatesBounds()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            using var scope = fixture.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();

            // Add 15 products with delays to establish ordering
            var productIds = new List<Guid>();
            for (var i = 0; i < 15; i++)
            {
                var pid = await CreateProductAsync($"Vợt Page {i}");
                await CreateSkuAsync(pid);
                productIds.Add(pid);
                await service.AddItemAsync(custId, pid);
                await Task.Delay(5);
            }

            // Page 1: pageSize 10 -> newest 10 items
            var page1 = await service.GetWishlistAsync(custId, page: 1, pageSize: 10);
            Assert.Equal(15, page1.TotalCount);
            Assert.Equal(10, page1.Items.Count);
            Assert.Equal(productIds[^1], page1.Items[0].ProductId); // newest is first

            // Page 2: pageSize 10 -> oldest 5 items
            var page2 = await service.GetWishlistAsync(custId, page: 2, pageSize: 10);
            Assert.Equal(15, page2.TotalCount);
            Assert.Equal(5, page2.Items.Count);
            Assert.Equal(productIds[0], page2.Items[^1].ProductId); // oldest is last

            // Page 3: empty
            var page3 = await service.GetWishlistAsync(custId, page: 3, pageSize: 10);
            Assert.Equal(15, page3.TotalCount);
            Assert.Empty(page3.Items);

            // Validation tests
            await Assert.ThrowsAsync<WishlistFailure>(() => service.GetWishlistAsync(custId, page: 0));
            await Assert.ThrowsAsync<WishlistFailure>(() => service.GetWishlistAsync(custId, page: 100001));
            await Assert.ThrowsAsync<WishlistFailure>(() => service.GetWishlistAsync(custId, pageSize: 0));
            await Assert.ThrowsAsync<WishlistFailure>(() => service.GetWishlistAsync(custId, pageSize: 51));
        }
    }

    // =========================================================================
    // 9. Full HTTP Lifecycle
    // =========================================================================
    [Fact]
    public async Task Customer_FullHttpLifecycle_WishlistEndpoints()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            var product = await CreateProductAsync("Vợt Yonex Nanoflare 1000Z");
            await CreateSkuAsync(product);

            // 1. Initially summary is empty
            var sum1 = await cust.GetFromJsonAsync<WishlistSummaryDto>("/api/v1/wishlist/summary");
            Assert.NotNull(sum1);
            Assert.Equal(0, sum1.TotalCount);
            Assert.Empty(sum1.ProductIds);

            // 2. PUT add item via HTTP
            using var putResp = await Send(cust, $"wishlist/items/{product}", method: HttpMethod.Put);
            Assert.Equal(HttpStatusCode.NoContent, putResp.StatusCode);

            // 3. GET summary reflects product
            var sum2 = await cust.GetFromJsonAsync<WishlistSummaryDto>("/api/v1/wishlist/summary");
            Assert.NotNull(sum2);
            Assert.Equal(1, sum2.TotalCount);
            Assert.Single(sum2.ProductIds, product);

            // 4. GET wishlist list reflects item
            var listResp = await cust.GetFromJsonAsync<WishlistPageDto>("/api/v1/wishlist");
            Assert.NotNull(listResp);
            Assert.Equal(1, listResp.TotalCount);
            var item = Assert.Single(listResp.Items);
            Assert.Equal(product, item.ProductId);
            Assert.Equal(WishlistAvailability.Available, item.Availability);
            Assert.NotNull(item.Product);
            Assert.Equal("Vợt Yonex Nanoflare 1000Z", item.Product.Title);

            // 5. DELETE item via HTTP
            using var delResp = await Send(cust, $"wishlist/items/{product}", method: HttpMethod.Delete);
            Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

            // 6. GET wishlist is now empty
            var listAfter = await cust.GetFromJsonAsync<WishlistPageDto>("/api/v1/wishlist");
            Assert.NotNull(listAfter);
            Assert.Equal(0, listAfter.TotalCount);
            Assert.Empty(listAfter.Items);
        }
    }

    // =========================================================================
    // 10. Migration Reapply Safety
    // =========================================================================
    [Fact]
    public async Task WishlistMigration_CanBeReappliedWithoutChangingExistingRows()
    {
        var (customer, customerId) = await CreateAuthenticatedCustomerClient();
        using (customer)
        {
            var productId = await CreateProductAsync("Vợt Migration Reapply");
            await CreateSkuAsync(productId);

            using var scope = fixture.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistService>();
            await service.AddItemAsync(customerId, productId);

            await using var db = await fixture.OpenDatabaseAsync();
            static async Task<DateTime> ReadCreatedAtAsync(NpgsqlConnection connection, Guid customerId, Guid productId)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT created_at FROM store.customer_wishlist_items WHERE customer_id = @customerId AND product_id = @productId",
                    connection);
                command.Parameters.AddWithValue("customerId", customerId);
                command.Parameters.AddWithValue("productId", productId);
                return (DateTime)(await command.ExecuteScalarAsync())!;
            }

            var before = await ReadCreatedAtAsync(db, customerId, productId);
            await using var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream("CustomerWishlistSchema.sql")
                ?? throw new InvalidOperationException("Missing CustomerWishlistSchema.sql");
            using var reader = new StreamReader(stream);
            var migrationSql = await reader.ReadToEndAsync();

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var migration = new NpgsqlCommand(migrationSql, db);
                await migration.ExecuteNonQueryAsync();
            }

            var after = await ReadCreatedAtAsync(db, customerId, productId);
            Assert.Equal(before, after);
        }
    }
}
