using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductManagementTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "A secure Staff/Admin pass 123!";
    private static string Email(string prefix) => $"{prefix}_{Guid.NewGuid():N}@example.invalid";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        return await client.SendAsync(request);
    }

    private static async Task<(HttpClient Client, Guid UserId)> CreateUserClientWithRole(CatalogFixture fixture, string roleKey, string prefix)
    {
        var email = Email(prefix);
        var userId = Guid.NewGuid();

        await using var db = await fixture.OpenDatabaseAsync();
        await using (var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @roleKey", db))
        {
            roleCmd.Parameters.AddWithValue("roleKey", roleKey);
            var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
            var hash = hasher.HashPassword(new object(), Password);

            await using var insertCmd = new NpgsqlCommand(
                "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status) " +
                "VALUES (@userId, @roleId, @email, @hash, @fullName, 'ACTIVE')", db);
            insertCmd.Parameters.AddWithValue("userId", userId);
            insertCmd.Parameters.AddWithValue("roleId", roleId);
            insertCmd.Parameters.AddWithValue("email", email);
            insertCmd.Parameters.AddWithValue("hash", hash);
            insertCmd.Parameters.AddWithValue("fullName", $"{roleKey} User");
            await insertCmd.ExecuteNonQueryAsync();
        }

        var client = fixture.CreateClient();
        using var loginRes = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
        return (client, userId);
    }

    private static async Task<HttpClient> CreateCustomerClient(CatalogFixture fixture)
    {
        var client = fixture.CreateClient();
        var email = Email("cust");
        using var reg = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Customer" });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task RBAC_enforces_proper_access_controls()
    {
        using var guest = fixture.CreateClient();
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_rbac");
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_rbac");
        using var customer = await CreateCustomerClient(fixture);

        // 1. Guest -> Staff endpoints -> 401
        var guestStaffRes = await guest.GetAsync("/api/v1/staff/products");
        Assert.Equal(HttpStatusCode.Unauthorized, guestStaffRes.StatusCode);

        // 2. Guest -> Admin endpoints -> 401
        var guestAdminRes = await guest.GetAsync("/api/v1/admin/products");
        Assert.Equal(HttpStatusCode.Unauthorized, guestAdminRes.StatusCode);

        // 3. Customer -> Staff endpoints -> 403
        var custStaffRes = await customer.GetAsync("/api/v1/staff/products");
        Assert.Equal(HttpStatusCode.Forbidden, custStaffRes.StatusCode);

        // 4. Customer -> Admin endpoints -> 403
        var custAdminRes = await customer.GetAsync("/api/v1/admin/products");
        Assert.Equal(HttpStatusCode.Forbidden, custAdminRes.StatusCode);

        // 5. Staff -> Admin endpoints -> 403 (Staff cannot set price or publish)
        var staffPriceRes = await Send(staff, "admin/skus/30000000-0000-0000-0000-000000000001/price", new { price = 1000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.Forbidden, staffPriceRes.StatusCode);

        var staffPublishRes = await Send(staff, "admin/products/20000000-0000-0000-0000-000000000001/publish", null, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, staffPublishRes.StatusCode);

        // 6. Admin -> Staff endpoints -> 403 (Admin is not STAFF)
        var adminStaffRes = await Send(admin, "staff/products", new { title = "Admin Product", brand = "Yonex" }, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, adminStaffRes.StatusCode);
    }

    [Fact]
    public async Task Complete_lifecycle_draft_to_pricing_to_publish_and_catalog()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_flow");
        var (admin, adminId) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_flow");

        var categoryId = Guid.Parse("10000000-0000-0000-0000-000000000001"); // Display
        var title = "Yonex Astrox 88D Pro " + Guid.NewGuid().ToString("N")[..6];

        // Step 1: Staff creates Draft product
        var createPayload = new
        {
            title,
            categoryId,
            description = "High performance badminton racket for front/back court."
        };

        using var createRes = await Send(staff, "staff/products", createPayload);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        Assert.NotNull(createRes.Headers.Location);

        var createdProduct = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var productId = createdProduct.GetProperty("id").GetGuid();
        Assert.Equal(title, createdProduct.GetProperty("title").GetString());
        Assert.Equal("DRAFT", createdProduct.GetProperty("publicationStatus").GetString());

        // Step 2: Product in DRAFT is NOT visible in public catalog
        using var guest = fixture.CreateClient();
        var catalogCheckBefore = await guest.GetAsync($"/api/v1/catalog/products/{productId}");
        Assert.Equal(HttpStatusCode.NotFound, catalogCheckBefore.StatusCode);

        // Step 3: Staff updates draft details
        var updatePayload = new
        {
            title = title + " Edition 2026",
            categoryId,
            description = "Updated high performance badminton racket."
        };
        using var updateRes = await Send(staff, $"staff/products/{productId}/draft", updatePayload, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updatedProduct = await updateRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(title + " Edition 2026", updatedProduct.GetProperty("title").GetString());
        Assert.Equal("Updated high performance badminton racket.", updatedProduct.GetProperty("description").GetString());

        // Step 4: Staff adds SKUs
        var skuCode1 = "AX88D-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var addSku1Payload = new
        {
            skuCode = skuCode1,
            optionLabel = "4U/G5 - Red"
        };
        using var addSkuRes1 = await Send(staff, $"staff/products/{productId}/skus", addSku1Payload);
        Assert.Equal(HttpStatusCode.Created, addSkuRes1.StatusCode);
        var sku1 = await addSkuRes1.Content.ReadFromJsonAsync<JsonElement>();
        var sku1Id = sku1.GetProperty("id").GetGuid();
        Assert.Equal(skuCode1, sku1.GetProperty("skuCode").GetString());
        Assert.Equal("ACTIVE", sku1.GetProperty("skuStatus").GetString());
        Assert.Equal(0m, sku1.GetProperty("regularPrice").GetDecimal()); // Price is 0 until Admin sets it

        // Step 5: Staff adds duplicate SKU code -> 409 Conflict
        using var dupSkuRes = await Send(staff, $"staff/products/{productId}/skus", addSku1Payload);
        Assert.Equal(HttpStatusCode.Conflict, dupSkuRes.StatusCode);

        // Step 6: Staff adds product images
        var addImg1Payload = new
        {
            fileKey = "yonex-astrox-front.webp",
            altText = "Front view of racket",
            displayOrder = 1
        };
        using var addImg1Res = await Send(staff, $"staff/products/{productId}/images", addImg1Payload);
        Assert.Equal(HttpStatusCode.Created, addImg1Res.StatusCode);
        var img1 = await addImg1Res.Content.ReadFromJsonAsync<JsonElement>();
        var img1Id = img1.GetProperty("id").GetGuid();
        Assert.Equal("yonex-astrox-front.webp", img1.GetProperty("fileKey").GetString());

        var addImg2Payload = new
        {
            fileKey = "yonex-astrox-back.webp",
            altText = "Back view of racket",
            displayOrder = 2
        };
        using var addImg2Res = await Send(staff, $"staff/products/{productId}/images", addImg2Payload);
        Assert.Equal(HttpStatusCode.Created, addImg2Res.StatusCode);
        var img2 = await addImg2Res.Content.ReadFromJsonAsync<JsonElement>();
        var img2Id = img2.GetProperty("id").GetGuid();

        // Staff deletes the second image
        using var delImg2Res = await Send(staff, $"staff/products/{productId}/images/{img2Id}", null, HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, delImg2Res.StatusCode);

        // Step 7: Admin attempts to Publish BEFORE setting price -> 400 CANNOT_PUBLISH
        using var prematurePublishRes = await Send(admin, $"admin/products/{productId}/publish", null, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.BadRequest, prematurePublishRes.StatusCode);
        var prematureErr = await prematurePublishRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CANNOT_PUBLISH", prematureErr.GetProperty("code").GetString());

        // Step 8: Admin sets SKU price
        var pricePayload = new
        {
            price = 3800000m
        };
        using var setPriceRes = await Send(admin, $"admin/skus/{sku1Id}/price", pricePayload, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, setPriceRes.StatusCode);
        var pricedSku = await setPriceRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3800000m, pricedSku.GetProperty("regularPrice").GetDecimal());

        // Verify audit log for price update in database
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var auditCmd = new NpgsqlCommand(
            "SELECT action, entity_type, entity_id, actor_id, after_data " +
            "FROM store.audit_logs WHERE action = 'SKU_PRICE_UPDATED' AND entity_id = @skuId", db))
        {
            auditCmd.Parameters.AddWithValue("skuId", sku1Id);
            await using var reader = await auditCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Audit log for SKU_PRICE_UPDATED must exist");
            Assert.Equal("ProductSku", reader.GetString(1));
            Assert.Equal(adminId, reader.GetGuid(3));
            var logPayload = reader.GetString(4);
            Assert.Contains("3800000", logPayload);
        }

        // Step 9: Admin Publishes product
        using var publishRes = await Send(admin, $"admin/products/{productId}/publish", null, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.OK, publishRes.StatusCode);
        var publishedProduct = await publishRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PUBLISHED", publishedProduct.GetProperty("publicationStatus").GetString());

        // Verify audit log for publish in database
        await using (var db = await fixture.OpenDatabaseAsync())
        await using (var auditCmd = new NpgsqlCommand(
            "SELECT action, entity_type, entity_id, actor_id " +
            "FROM store.audit_logs WHERE action = 'PRODUCT_PUBLISHED' AND entity_id = @prodId", db))
        {
            auditCmd.Parameters.AddWithValue("prodId", productId);
            await using var reader = await auditCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Audit log for PRODUCT_PUBLISHED must exist");
            Assert.Equal("Product", reader.GetString(1));
            Assert.Equal(adminId, reader.GetGuid(3));
        }

        // Step 10: Staff can no longer edit the draft, add image, or delete image of a published product -> 400 INVALID_STATE
        using var modifyPublishedRes = await Send(staff, $"staff/products/{productId}/draft", new { title = "Illegal edit" }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, modifyPublishedRes.StatusCode);
        var invalidStateErr = await modifyPublishedRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_STATE", invalidStateErr.GetProperty("code").GetString());

        using var addImgPublishedRes = await Send(staff, $"staff/products/{productId}/images", new { fileKey = "illegal.webp", displayOrder = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, addImgPublishedRes.StatusCode);
        var invalidImgAddErr = await addImgPublishedRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_STATE", invalidImgAddErr.GetProperty("code").GetString());

        using var delImgPublishedRes = await Send(staff, $"staff/products/{productId}/images/{img1Id}", null, HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.BadRequest, delImgPublishedRes.StatusCode);
        var invalidImgDelErr = await delImgPublishedRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_STATE", invalidImgDelErr.GetProperty("code").GetString());

        // Step 11: Public catalog now displays the published product with active image
        using var publicDetailRes = await guest.GetAsync($"/api/v1/catalog/products/{productId}");
        Assert.Equal(HttpStatusCode.OK, publicDetailRes.StatusCode);
        var publicDetail = await publicDetailRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(title + " Edition 2026", publicDetail.GetProperty("title").GetString());
        var images = publicDetail.GetProperty("images").EnumerateArray().ToArray();
        Assert.Single(images); // Only active img1, deleted img2 is omitted
        Assert.Equal("yonex-astrox-front.webp", images[0].GetProperty("fileKey").GetString());

        // Also check search/list in public catalog
        using var publicListRes = await guest.GetAsync($"/api/v1/catalog/products?q={Uri.EscapeDataString(title)}");
        Assert.Equal(HttpStatusCode.OK, publicListRes.StatusCode);
        var publicList = await publicListRes.Content.ReadFromJsonAsync<JsonElement>();
        var items = publicList.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(title + " Edition 2026", items[0].GetProperty("title").GetString());
        Assert.Equal("yonex-astrox-front.webp", items[0].GetProperty("imageUrl").GetString());
        Assert.Equal(3800000m, items[0].GetProperty("minPrice").GetDecimal());
    }

    [Fact]
    public async Task Admin_price_update_validates_positive_price()
    {
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_validation");

        // Price = 0 or negative must fail validation
        var zeroPrice = new { price = 0m };
        using var resZero = await Send(admin, "admin/skus/30000000-0000-0000-0000-000000000001/price", zeroPrice, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, resZero.StatusCode);

        var negPrice = new { price = -500m };
        using var resNeg = await Send(admin, "admin/skus/30000000-0000-0000-0000-000000000001/price", negPrice, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.BadRequest, resNeg.StatusCode);
    }

    [Fact]
    public async Task Concurrent_publish_requests_only_one_succeeds()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_pub");
        var (admin1, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_race1");
        var (admin2, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_race2");

        // 1. Staff creates draft
        var prodTitle = "Race Publish Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff, "staff/products", new { title = prodTitle });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        // 2. Staff adds SKU
        var skuCode = "RACE-PUB-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var skuRes = await Send(staff, $"staff/products/{prodId}/skus", new { skuCode, optionLabel = "Standard" });
        Assert.Equal(HttpStatusCode.Created, skuRes.StatusCode);
        var sku = await skuRes.Content.ReadFromJsonAsync<JsonElement>();
        var skuId = sku.GetProperty("id").GetGuid();

        // 3. Admin sets price
        using var priceRes = await Send(admin1, $"admin/skus/{skuId}/price", new { price = 2500000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, priceRes.StatusCode);

        // 4. Send two publish requests concurrently
        var task1 = Send(admin1, $"admin/products/{prodId}/publish", null, HttpMethod.Post);
        var task2 = Send(admin2, $"admin/products/{prodId}/publish", null, HttpMethod.Post);

        var responses = await Task.WhenAll(task1, task2);
        using var res1 = responses[0];
        using var res2 = responses[1];

        var statusCodes = new[] { res1.StatusCode, res2.StatusCode };
        Assert.Contains(HttpStatusCode.OK, statusCodes);
        Assert.Contains(HttpStatusCode.BadRequest, statusCodes);

        var failedRes = res1.StatusCode == HttpStatusCode.BadRequest ? res1 : res2;
        var failErr = await failedRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CANNOT_PUBLISH", failErr.GetProperty("code").GetString());

        // 5. Ensure EXACTLY 1 PRODUCT_PUBLISHED audit log exists in the database
        await using var db = await fixture.OpenDatabaseAsync();
        await using var auditCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM store.audit_logs WHERE action = 'PRODUCT_PUBLISHED' AND entity_id = @prodId", db);
        auditCmd.Parameters.AddWithValue("prodId", prodId);
        var count = (long)(await auditCmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Concurrent_duplicate_sku_creation_returns_409_conflict_never_500()
    {
        var (staff1, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_sku1");
        var (staff2, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_sku2");

        // 1. Create a draft product
        var prodTitle = "Race SKU Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff1, "staff/products", new { title = prodTitle });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        // 2. Concurrently insert the EXACT same SKU code
        var dupSkuCode = "DUP-RACE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var payload = new { skuCode = dupSkuCode, optionLabel = "Race Option" };

        var task1 = Send(staff1, $"staff/products/{prodId}/skus", payload);
        var task2 = Send(staff2, $"staff/products/{prodId}/skus", payload);

        var responses = await Task.WhenAll(task1, task2);
        using var res1 = responses[0];
        using var res2 = responses[1];

        var statusCodes = new[] { res1.StatusCode, res2.StatusCode };
        Assert.Contains(HttpStatusCode.Created, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        // Neither request should ever return 500
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statusCodes);

        var conflictRes = res1.StatusCode == HttpStatusCode.Conflict ? res1 : res2;
        var errBody = await conflictRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONFLICT", errBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Catalog_orders_images_by_created_at_when_display_order_is_equal()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_order_test");
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_order_test");

        // 1. Create product & SKU & price
        var title = "Order Test Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff, "staff/products", new { title });
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        var skuCode = "ORDER-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var skuRes = await Send(staff, $"staff/products/{prodId}/skus", new { skuCode, optionLabel = "Default" });
        var sku = await skuRes.Content.ReadFromJsonAsync<JsonElement>();
        var skuId = sku.GetProperty("id").GetGuid();

        using var priceRes = await Send(admin, $"admin/skus/{skuId}/price", new { price = 1500000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, priceRes.StatusCode);

        // 2. Add two images with EQUAL displayOrder (both 1), created sequentially
        using var img1Res = await Send(staff, $"staff/products/{prodId}/images", new { fileKey = "img_earlier.webp", displayOrder = 1 });
        Assert.Equal(HttpStatusCode.Created, img1Res.StatusCode);

        // Short sleep to ensure earlier created_at
        await Task.Delay(50);

        using var img2Res = await Send(staff, $"staff/products/{prodId}/images", new { fileKey = "img_later.webp", displayOrder = 1 });
        Assert.Equal(HttpStatusCode.Created, img2Res.StatusCode);

        // 3. Publish product
        using var pubRes = await Send(admin, $"admin/products/{prodId}/publish", null, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);

        // 4. Public catalog should order earlier created image first
        using var guest = fixture.CreateClient();
        using var detailRes = await guest.GetAsync($"/api/v1/catalog/products/{prodId}");
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
        var detail = await detailRes.Content.ReadFromJsonAsync<JsonElement>();
        var images = detail.GetProperty("images").EnumerateArray().ToArray();

        Assert.Equal(2, images.Length);
        Assert.Equal("img_earlier.webp", images[0].GetProperty("fileKey").GetString());
        Assert.Equal("img_later.webp", images[1].GetProperty("fileKey").GetString());
    }

    [Fact]
    public async Task Concurrent_publish_and_staff_add_zero_price_sku_preserves_invariants()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_sku_pub");
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_race_sku_pub");

        // 1. Create a draft product with 1 valid priced SKU
        var title = "Race Invariant Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff, "staff/products", new { title });
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        var skuCode1 = "VALID-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var skuRes1 = await Send(staff, $"staff/products/{prodId}/skus", new { skuCode = skuCode1, optionLabel = "Priced" });
        var sku1 = await skuRes1.Content.ReadFromJsonAsync<JsonElement>();
        var sku1Id = sku1.GetProperty("id").GetGuid();

        using var priceRes = await Send(admin, $"admin/skus/{sku1Id}/price", new { price = 2000000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, priceRes.StatusCode);

        // 2. Concurrently: Admin publishes vs Staff adds new unpriced (0 regular price) SKU
        var skuCode2 = "ZERO-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var taskPublish = Send(admin, $"admin/products/{prodId}/publish", null, HttpMethod.Post);
        var taskAddSku = Send(staff, $"staff/products/{prodId}/skus", new { skuCode = skuCode2, optionLabel = "Unpriced" });

        var responses = await Task.WhenAll(taskPublish, taskAddSku);
        using var resPub = responses[0];
        using var resSku = responses[1];

        // Case A: Staff won lock first -> SKU added (201), then Publish saw unpriced active SKU and failed (400 CANNOT_PUBLISH)
        // Case B: Admin won lock first -> Product published (200), then Staff saw PUBLISHED status and failed (400 INVALID_STATE)
        if (resPub.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.BadRequest, resSku.StatusCode);
            var err = await resSku.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_STATE", err.GetProperty("code").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, resPub.StatusCode);
            var err = await resPub.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CANNOT_PUBLISH", err.GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Created, resSku.StatusCode);
        }

        // 3. Database Invariant verification:
        // A PUBLISHED product must NEVER have any active SKU with regular_price <= 0
        await using var db = await fixture.OpenDatabaseAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT p.publication_status, COUNT(s.id) " +
            "FROM store.products p " +
            "LEFT JOIN store.product_skus s ON s.product_id = p.id AND s.sku_status = 'ACTIVE' AND s.regular_price <= 0 " +
            "WHERE p.id = @prodId " +
            "GROUP BY p.publication_status", db);
        checkCmd.Parameters.AddWithValue("prodId", prodId);

        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var publicationStatus = reader.GetString(0);
        var unpricedSkuCount = reader.GetInt64(1);

        if (publicationStatus == "PUBLISHED")
        {
            Assert.Equal(0L, unpricedSkuCount);
        }
        else
        {
            Assert.Equal("DRAFT", publicationStatus);
        }
    }

    [Fact]
    public async Task Concurrent_publish_and_staff_edit_or_image_preserves_invariants()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_edit");
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_race_edit");

        // 1. Create a draft product with priced SKU
        var title = "Race Edit Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff, "staff/products", new { title });
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        var skuCode = "RACE-EDIT-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var skuRes = await Send(staff, $"staff/products/{prodId}/skus", new { skuCode, optionLabel = "Default" });
        var sku = await skuRes.Content.ReadFromJsonAsync<JsonElement>();
        var skuId = sku.GetProperty("id").GetGuid();

        using var priceRes = await Send(admin, $"admin/skus/{skuId}/price", new { price = 1800000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, priceRes.StatusCode);

        // 2. Concurrently: Admin publishes vs Staff adds an image
        var taskPublish = Send(admin, $"admin/products/{prodId}/publish", null, HttpMethod.Post);
        var taskAddImg = Send(staff, $"staff/products/{prodId}/images", new { fileKey = "race_img.webp", displayOrder = 1 });

        var responses = await Task.WhenAll(taskPublish, taskAddImg);
        using var resPub = responses[0];
        using var resImg = responses[1];

        // Either Staff completes before publish (201 Created and Publish 200 OK),
        // Or Admin publishes first (200 OK) and Staff gets 400 INVALID_STATE
        Assert.Equal(HttpStatusCode.OK, resPub.StatusCode);
        var imgStatus = resImg.StatusCode;
        Assert.True(imgStatus == HttpStatusCode.Created || imgStatus == HttpStatusCode.BadRequest, $"Status was {imgStatus}");

        if (imgStatus == HttpStatusCode.BadRequest)
        {
            var err = await resImg.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_STATE", err.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Concurrent_publish_and_staff_update_draft_preserves_invariants()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_upd_draft");
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_race_upd_draft");

        var title = "Race Update Draft Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff, "staff/products", new { title });
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        var skuCode = "RACE-UPD-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var skuRes = await Send(staff, $"staff/products/{prodId}/skus", new { skuCode, optionLabel = "Default" });
        var sku = await skuRes.Content.ReadFromJsonAsync<JsonElement>();
        var skuId = sku.GetProperty("id").GetGuid();

        using var priceRes = await Send(admin, $"admin/skus/{skuId}/price", new { price = 2200000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, priceRes.StatusCode);

        // Concurrently: Admin publishes vs Staff updates draft title
        var taskPublish = Send(admin, $"admin/products/{prodId}/publish", null, HttpMethod.Post);
        var taskUpdate = Send(staff, $"staff/products/{prodId}/draft", new { title = title + " Updated" }, HttpMethod.Patch);

        var responses = await Task.WhenAll(taskPublish, taskUpdate);
        using var resPub = responses[0];
        using var resUpd = responses[1];

        Assert.Equal(HttpStatusCode.OK, resPub.StatusCode);
        var updStatus = resUpd.StatusCode;
        Assert.True(updStatus == HttpStatusCode.OK || updStatus == HttpStatusCode.BadRequest, $"Status was {updStatus}");

        if (updStatus == HttpStatusCode.BadRequest)
        {
            var err = await resUpd.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_STATE", err.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Concurrent_publish_and_staff_delete_image_preserves_invariants()
    {
        var (staff, _) = await CreateUserClientWithRole(fixture, "STAFF", "staff_race_del_img");
        var (admin, _) = await CreateUserClientWithRole(fixture, "ADMIN", "admin_race_del_img");

        var title = "Race Delete Img Racket " + Guid.NewGuid().ToString("N")[..6];
        using var createRes = await Send(staff, "staff/products", new { title });
        var prod = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var prodId = prod.GetProperty("id").GetGuid();

        var skuCode = "RACE-DELIMG-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var skuRes = await Send(staff, $"staff/products/{prodId}/skus", new { skuCode, optionLabel = "Default" });
        var sku = await skuRes.Content.ReadFromJsonAsync<JsonElement>();
        var skuId = sku.GetProperty("id").GetGuid();

        using var priceRes = await Send(admin, $"admin/skus/{skuId}/price", new { price = 2100000m }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, priceRes.StatusCode);

        // Add image first
        using var imgRes = await Send(staff, $"staff/products/{prodId}/images", new { fileKey = "img_to_delete.webp", displayOrder = 1 });
        Assert.Equal(HttpStatusCode.Created, imgRes.StatusCode);
        var img = await imgRes.Content.ReadFromJsonAsync<JsonElement>();
        var imgId = img.GetProperty("id").GetGuid();

        // Concurrently: Admin publishes vs Staff deletes the image
        var taskPublish = Send(admin, $"admin/products/{prodId}/publish", null, HttpMethod.Post);
        var taskDelImg = Send(staff, $"staff/products/{prodId}/images/{imgId}", null, HttpMethod.Delete);

        var responses = await Task.WhenAll(taskPublish, taskDelImg);
        using var resPub = responses[0];
        using var resDel = responses[1];

        Assert.Equal(HttpStatusCode.OK, resPub.StatusCode);
        var delStatus = resDel.StatusCode;
        Assert.True(delStatus == HttpStatusCode.NoContent || delStatus == HttpStatusCode.BadRequest, $"Status was {delStatus}");

        if (delStatus == HttpStatusCode.BadRequest)
        {
            var err = await resDel.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("INVALID_STATE", err.GetProperty("code").GetString());
        }
    }
}
