using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Reviews;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using HighConcurrencyCommerce.Infrastructure.Reviews;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductReviewTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private static string Email() => $"review_cust_{Guid.NewGuid():N}@example.invalid";

    [Theory]
    [InlineData("rating=0")]
    [InlineData("rating=6")]
    [InlineData("page=2147483647")]
    public async Task Public_query_rejects_invalid_rating_or_page(string query)
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync($"/api/v1/catalog/products/{Guid.NewGuid()}/reviews?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("ARCHIVED")]
    public async Task Public_reviews_reject_non_public_products_even_with_existing_reviews(string status)
    {
        var (customer, _) = await CreateCustomerAsync();
        var product = await CreateProductAsync();
        await CreateDeliveredOrderAsync(customer, await CreateSkuAsync(product));
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();
        await service.CreateReviewAsync(customer, product, new() { Rating = 5, Content = "Sản phẩm chất lượng rất tốt." });
        await using var connection = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("UPDATE store.products SET publication_status = @status WHERE id = @id", connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", product);
        await command.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<ProductReviewFailure>(() => service.GetPublicReviewsAsync(product, new()));
        Assert.Equal("PRODUCT_NOT_FOUND", failure.Code);
        await Assert.ThrowsAsync<ProductReviewFailure>(() => service.GetProductRatingSummaryAsync(product));
        using var client = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/catalog/products/{product}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/catalog/products/{Guid.NewGuid()}/reviews")).StatusCode);
    }

    [Fact]
    public async Task Public_page_keeps_same_snapshot_when_review_is_hidden_between_queries()
    {
        var (customer, _) = await CreateCustomerAsync();
        var (moderator, _) = await CreateModeratorAsync();
        var product = await CreateProductAsync();
        await CreateDeliveredOrderAsync(customer, await CreateSkuAsync(product));
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();
        var review = await service.CreateReviewAsync(customer, product, new() { Rating = 5, Content = "Sản phẩm chất lượng rất tốt." });
        var interceptor = new BeforeReviewPageInterceptor(async () =>
        {
            using var writerScope = fixture.Services.CreateScope();
            await writerScope.ServiceProvider.GetRequiredService<IProductReviewService>().ModerateReviewAsync(
                moderator, "STAFF", review.Id, new() { Action = "HIDE", Reason = "Kiểm tra nội dung" });
        });
        var connectionString = scope.ServiceProvider.GetRequiredService<StoreDbContext>().Database.GetConnectionString();
        await using var readerDb = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql(connectionString).AddInterceptors(interceptor).Options);
        var reader = new PostgresProductReviewService(readerDb, TimeProvider.System);
        var page = await reader.GetPublicReviewsAsync(product, new() { Rating = 5 });
        Assert.True(interceptor.Triggered);
        Assert.Equal(1, page.Summary.ReviewCount);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(review.Id, Assert.Single(page.Items).Id);
        var after = await reader.GetPublicReviewsAsync(product, new());
        Assert.Empty(after.Items);
        Assert.Equal(0, after.Summary.ReviewCount);
    }

    private sealed class BeforeReviewPageInterceptor(Func<Task> mutate) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("product_reviews") && command.CommandText.Contains("LIMIT"))
            {
                Triggered = true;
                await mutate();
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_mutation_racing_moderation_preserves_hidden_or_deleted_state(bool delete)
    {
        var (customer, _) = await CreateCustomerAsync();
        var (moderator, _) = await CreateModeratorAsync();
        var product = await CreateProductAsync();
        await CreateDeliveredOrderAsync(customer, await CreateSkuAsync(product));
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();
        var review = await service.CreateReviewAsync(customer, product, new() { Rating = 5, Content = "Sản phẩm chất lượng rất tốt." });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task MutateOwner()
        {
            using var ownScope = fixture.Services.CreateScope();
            var ownService = ownScope.ServiceProvider.GetRequiredService<IProductReviewService>();
            await start.Task;
            if (delete) await ownService.DeleteReviewAsync(customer, review.Id);
            else await ownService.UpdateReviewAsync(customer, review.Id, new() { Rating = 2, Content = "Nội dung cập nhật của khách hàng." });
        }
        async Task Moderate()
        {
            using var modScope = fixture.Services.CreateScope();
            var modService = modScope.ServiceProvider.GetRequiredService<IProductReviewService>();
            await start.Task;
            try { await modService.ModerateReviewAsync(moderator, "STAFF", review.Id, new() { Action = "HIDE", Reason = "Kiểm tra nội dung" }); }
            catch (ProductReviewFailure ex) when (delete && ex.Code == "REVIEW_NOT_FOUND") { }
        }
        var owner = MutateOwner();
        var moderation = Moderate();
        start.SetResult();
        await Task.WhenAll(owner, moderation).WaitAsync(TimeSpan.FromSeconds(20));
        using var checkScope = fixture.Services.CreateScope();
        var row = await checkScope.ServiceProvider.GetRequiredService<StoreDbContext>().ProductReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id);
        if (delete) Assert.NotNull(row.DeletedAt);
        else { Assert.Equal("HIDDEN", row.ReviewStatus); Assert.Equal(2, row.Rating); }
        Assert.Empty((await service.GetPublicReviewsAsync(product, new())).Items);
    }

    private async Task<(Guid CustomerId, string FullName)> CreateCustomerAsync(string? name = null)
    {
        var id = Guid.NewGuid();
        var email = Email();
        var fullName = name ?? "Nguyễn Văn Khách";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = 'CUSTOMER'", db);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status, email_verified_at) " +
            "VALUES (@id, @roleId, @email, 'dummy_hash', @fullName, 'ACTIVE', now())", db);
        insertCmd.Parameters.AddWithValue("id", id);
        insertCmd.Parameters.AddWithValue("roleId", roleId);
        insertCmd.Parameters.AddWithValue("email", email);
        insertCmd.Parameters.AddWithValue("fullName", fullName);
        await insertCmd.ExecuteNonQueryAsync();

        return (id, fullName);
    }

    private async Task<(Guid ModeratorId, string FullName)> CreateModeratorAsync(string roleKey = "STAFF")
    {
        var id = Guid.NewGuid();
        var email = $"mod_{Guid.NewGuid():N}@example.invalid";
        var fullName = roleKey == "ADMIN" ? "Quản trị viên Review" : "Nhân viên Kiểm duyệt";
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCmd = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key = @roleKey", db);
        roleCmd.Parameters.AddWithValue("roleKey", roleKey);
        var roleId = (Guid)(await roleCmd.ExecuteScalarAsync())!;

        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO store.user_accounts (id, role_id, email, password_hash, full_name, account_status, email_verified_at) " +
            "VALUES (@id, @roleId, @email, 'dummy_hash', @fullName, 'ACTIVE', now())", db);
        insertCmd.Parameters.AddWithValue("id", id);
        insertCmd.Parameters.AddWithValue("roleId", roleId);
        insertCmd.Parameters.AddWithValue("email", email);
        insertCmd.Parameters.AddWithValue("fullName", fullName);
        await insertCmd.ExecuteNonQueryAsync();

        return (id, fullName);
    }

    private async Task<Guid> CreateProductAsync(string title = "Vợt cầu lông Yonex Review Test", string publicationStatus = "PUBLISHED")
    {
        var productId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var adminCmd = new NpgsqlCommand("SELECT id FROM store.user_accounts LIMIT 1", db);
        var actorId = (Guid)(await adminCmd.ExecuteScalarAsync())!;

        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO store.products (id, title, publication_status, created_by, published_by, published_at) " +
            "VALUES (@id, @title, @status, @actorId, @actorId, now())", db);
        insertCmd.Parameters.AddWithValue("id", productId);
        insertCmd.Parameters.AddWithValue("title", title);
        insertCmd.Parameters.AddWithValue("status", publicationStatus);
        insertCmd.Parameters.AddWithValue("actorId", actorId);
        await insertCmd.ExecuteNonQueryAsync();

        return productId;
    }

    private async Task<Guid> CreateSkuAsync(Guid productId, string codePrefix = "REV-SKU")
    {
        var skuId = Guid.NewGuid();
        await using var db = await fixture.OpenDatabaseAsync();
        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO store.product_skus (id, product_id, sku_code, option_label, regular_price, sku_status) " +
            "VALUES (@id, @productId, @code, '4U G5', 2500000, 'ACTIVE'); " +
            "INSERT INTO store.inventory_balances (id, product_sku_id, physical_qty, available_qty, reserved_qty, allocated_qty, revision) " +
            "VALUES (gen_random_uuid(), @id, 100, 100, 0, 0, 0);", db);
        insertCmd.Parameters.AddWithValue("id", skuId);
        insertCmd.Parameters.AddWithValue("productId", productId);
        insertCmd.Parameters.AddWithValue("code", $"{codePrefix}-{skuId:N}"[..20].ToUpperInvariant());
        await insertCmd.ExecuteNonQueryAsync();

        return skuId;
    }

    private async Task<(Guid OrderId, Guid OrderItemId)> CreateDeliveredOrderAsync(
        Guid customerId,
        Guid skuId,
        string orderStatus = "COMPLETED",
        string shipmentStatus = "DELIVERED",
        bool deliveredAtSet = true)
    {
        var orderId = Guid.NewGuid();
        var orderItemId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();

        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO store.sales_orders (id, customer_id, order_number, order_status, payment_method, shipping_address_snapshot, total_amount, subtotal_amount, voucher_discount_amount, deadline_at) " +
            "VALUES (@orderId, @customerId, @orderNumber, @orderStatus, 'COD', '{\"recipientName\": \"Test\", \"phone\": \"0901234567\", \"addressLine\": \"123 Str\", \"province\": \"HCM\"}'::jsonb, 2500000, 2500000, 0, now() + interval '1 day'); " +
            "INSERT INTO store.sales_order_items (id, order_id, product_sku_id, quantity, unit_price, product_title_snapshot, sku_code_snapshot, sku_option_label_snapshot) " +
            "VALUES (@orderItemId, @orderId, @skuId, 1, 2500000, 'Snapshot Product Title', 'SKU-SNAP', '4U'); " +
            "INSERT INTO store.shipments (id, order_id, shipment_status, delivered_at) " +
            "VALUES (@shipmentId, @orderId, @shipmentStatus, @deliveredAt);", db);

        cmd.Parameters.AddWithValue("orderId", orderId);
        cmd.Parameters.AddWithValue("customerId", customerId);
        cmd.Parameters.AddWithValue("orderNumber", $"ORD-{Guid.NewGuid():N}"[..18].ToUpperInvariant());
        cmd.Parameters.AddWithValue("orderStatus", orderStatus);
        cmd.Parameters.AddWithValue("orderItemId", orderItemId);
        cmd.Parameters.AddWithValue("skuId", skuId);
        cmd.Parameters.AddWithValue("shipmentId", shipmentId);
        cmd.Parameters.AddWithValue("shipmentStatus", shipmentStatus);
        cmd.Parameters.AddWithValue("deliveredAt", deliveredAtSet ? DateTime.UtcNow : (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
        return (orderId, orderItemId);
    }

    [Fact]
    public async Task GetEligibility_Returns_NoDeliveredOrder_WhenCustomerHasNoPurchases()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        await CreateSkuAsync(productId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var eligibility = await service.GetEligibilityAsync(customerId, productId);

        Assert.False(eligibility.Eligible);
        Assert.Equal("NO_DELIVERED_ORDER", eligibility.ReasonCode);
        Assert.Null(eligibility.ExistingReview);
    }

    [Fact]
    public async Task GetEligibility_Returns_NoDeliveredOrder_WhenOrderNotCompletedOrNotDelivered()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);

        // Case 1: Order status not COMPLETED (e.g. CONFIRMED)
        await CreateDeliveredOrderAsync(customerId, skuId, orderStatus: "CONFIRMED", shipmentStatus: "DELIVERED");

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var eligibility1 = await service.GetEligibilityAsync(customerId, productId);
        Assert.False(eligibility1.Eligible);
        Assert.Equal("NO_DELIVERED_ORDER", eligibility1.ReasonCode);

        // Case 2: Order COMPLETED but shipment not DELIVERED (e.g. SHIPPING)
        var (cust2, _) = await CreateCustomerAsync();
        await CreateDeliveredOrderAsync(cust2, skuId, orderStatus: "COMPLETED", shipmentStatus: "SHIPPING", deliveredAtSet: false);

        var eligibility2 = await service.GetEligibilityAsync(cust2, productId);
        Assert.False(eligibility2.Eligible);
        Assert.Equal("NO_DELIVERED_ORDER", eligibility2.ReasonCode);
    }

    [Fact]
    public async Task GetEligibility_Returns_Eligible_WhenOrderCompletedAndShipmentDelivered()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);

        await CreateDeliveredOrderAsync(customerId, skuId, orderStatus: "COMPLETED", shipmentStatus: "DELIVERED");

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var eligibility = await service.GetEligibilityAsync(customerId, productId);
        Assert.True(eligibility.Eligible);
        Assert.Null(eligibility.ReasonCode);
        Assert.Null(eligibility.ExistingReview);
    }

    [Theory]
    [InlineData(0, "Nội dung nhận xét hợp lệ dài hơn 10 ký tự.")]
    [InlineData(6, "Nội dung nhận xét hợp lệ dài hơn 10 ký tự.")]
    [InlineData(5, "Ngắn")]
    [InlineData(5, "   ")]
    public async Task CreateReview_ThrowsValidationError_ForInvalidRatingOrContent(short rating, string content)
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var ex = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
            {
                Rating = rating,
                Content = content
            }));

        Assert.Equal("VALIDATION_ERROR", ex.Code);
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task CreateReview_ThrowsReviewNotEligible_WhenCustomerNotEligible()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        await CreateSkuAsync(productId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var ex = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
            {
                Rating = 5,
                Content = "Vợt cầu lông đánh rất tốt và êm tay!"
            }));

        Assert.Equal("REVIEW_NOT_ELIGIBLE", ex.Code);
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task CreateReview_ThrowsProductNotFound_WhenProductDoesNotExistOrNotPublished()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var randomProductId = Guid.NewGuid();

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var ex1 = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.CreateReviewAsync(customerId, randomProductId, new CreateProductReviewRequest
            {
                Rating = 5,
                Content = "Vợt cầu lông đánh rất tốt và êm tay!"
            }));
        Assert.Equal("PRODUCT_NOT_FOUND", ex1.Code);
        Assert.Equal(404, ex1.Status);

        // Draft product
        var draftProduct = await CreateProductAsync("Draft Racket", publicationStatus: "DRAFT");
        var draftSku = await CreateSkuAsync(draftProduct);
        await CreateDeliveredOrderAsync(customerId, draftSku);

        var ex2 = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.CreateReviewAsync(customerId, draftProduct, new CreateProductReviewRequest
            {
                Rating = 5,
                Content = "Vợt cầu lông đánh rất tốt và êm tay!"
            }));
        Assert.Equal("PRODUCT_NOT_FOUND", ex2.Code);
        Assert.Equal(404, ex2.Status);
    }

    [Fact]
    public async Task CreateReview_Succeeds_WhenEligible_AndQualifyingOrderItemLinked()
    {
        var (customerId, _) = await CreateCustomerAsync("Nguyễn Thị Test");
        var productId = await CreateProductAsync("Vợt Yonex Astrox 88D Pro");
        var skuId = await CreateSkuAsync(productId);
        var (_, orderItemId) = await CreateDeliveredOrderAsync(customerId, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var result = await service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
        {
            Rating = 5,
            Content = "Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!"
        });

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(productId, result.ProductId);
        Assert.Equal("Vợt Yonex Astrox 88D Pro", result.ProductTitle);
        Assert.Equal(5, result.Rating);
        Assert.Equal("Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!", result.Content);
        Assert.Equal("PUBLISHED", result.ReviewStatus);
        Assert.Null(result.ModeratedAt);
        Assert.Null(result.ModerationReason);

        // Verify DB record directly
        await using var db = await fixture.OpenDatabaseAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT customer_id, product_id, qualifying_order_item_id, rating, content, review_status, deleted_at " +
            "FROM store.product_reviews WHERE id = @id", db);
        checkCmd.Parameters.AddWithValue("id", result.Id);

        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(customerId, reader.GetGuid(0));
        Assert.Equal(productId, reader.GetGuid(1));
        Assert.Equal(orderItemId, reader.GetGuid(2));
        Assert.Equal(5, reader.GetInt16(3));
        Assert.Equal("Vợt đập cầu rất thoát tay, lưới căng chuẩn chỉ!", reader.GetString(4));
        Assert.Equal("PUBLISHED", reader.GetString(5));
        Assert.True(reader.IsDBNull(6));
    }

    [Fact]
    public async Task CreateReview_ThrowsReviewAlreadyExists_WhenActiveReviewPresent()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);
        await CreateDeliveredOrderAsync(customerId, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        await service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
        {
            Rating = 5,
            Content = "Đánh giá lần đầu tiên cho sản phẩm này."
        });

        // Second creation attempt
        var ex = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
            {
                Rating = 4,
                Content = "Cố tình đánh giá lần thứ hai xem có lỗi không."
            }));

        Assert.Equal("REVIEW_ALREADY_EXISTS", ex.Code);
        Assert.Equal(409, ex.Status);

        // Check eligibility now returns ALREADY_REVIEWED with existing review
        var eligibility = await service.GetEligibilityAsync(customerId, productId);
        Assert.False(eligibility.Eligible);
        Assert.Equal("ALREADY_REVIEWED", eligibility.ReasonCode);
        Assert.NotNull(eligibility.ExistingReview);
        Assert.Equal(5, eligibility.ExistingReview!.Rating);
    }

    [Fact]
    public async Task UpdateReview_OwnerOnly_UpdatesRatingAndContent()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);
        await CreateDeliveredOrderAsync(customerId, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var created = await service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
        {
            Rating = 3,
            Content = "Cảm giác đánh bình thường, chưa quen tay lắm."
        });

        var updated = await service.UpdateReviewAsync(customerId, created.Id, new UpdateProductReviewRequest
        {
            Rating = 5,
            Content = "Sau 1 tuần đánh quen tay thì thấy đập rất sướng và trợ lực tốt!"
        });

        Assert.Equal(5, updated.Rating);
        Assert.Equal("Sau 1 tuần đánh quen tay thì thấy đập rất sướng và trợ lực tốt!", updated.Content);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task UpdateReview_ThrowsReviewNotFound_WhenNonOwnerOrDeleted()
    {
        var (ownerId, _) = await CreateCustomerAsync();
        var (strangerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);
        await CreateDeliveredOrderAsync(ownerId, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var created = await service.CreateReviewAsync(ownerId, productId, new CreateProductReviewRequest
        {
            Rating = 4,
            Content = "Đánh giá của chủ sở hữu đơn hàng."
        });

        // Stranger attempts update
        var exStranger = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.UpdateReviewAsync(strangerId, created.Id, new UpdateProductReviewRequest
            {
                Rating = 1,
                Content = "Người lạ cố tình sửa đánh giá người khác."
            }));
        Assert.Equal("REVIEW_NOT_FOUND", exStranger.Code);
        Assert.Equal(404, exStranger.Status);

        // Delete review then attempt update
        await service.DeleteReviewAsync(ownerId, created.Id);

        var exDeleted = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.UpdateReviewAsync(ownerId, created.Id, new UpdateProductReviewRequest
            {
                Rating = 5,
                Content = "Cố sửa sau khi đã xóa mềm đánh giá."
            }));
        Assert.Equal("REVIEW_NOT_FOUND", exDeleted.Code);
        Assert.Equal(404, exDeleted.Status);
    }

    [Fact]
    public async Task DeleteReview_OwnerOnly_SoftDeletes_AndAllowsNewReview()
    {
        var (ownerId, _) = await CreateCustomerAsync();
        var (strangerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);
        await CreateDeliveredOrderAsync(ownerId, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var created = await service.CreateReviewAsync(ownerId, productId, new CreateProductReviewRequest
        {
            Rating = 2,
            Content = "Vợt hơi nặng đầu so với mong đợi của mình."
        });

        // Stranger cannot delete
        var ex = await Assert.ThrowsAsync<ProductReviewFailure>(() =>
            service.DeleteReviewAsync(strangerId, created.Id));
        Assert.Equal("REVIEW_NOT_FOUND", ex.Code);

        // Owner deletes
        await service.DeleteReviewAsync(ownerId, created.Id);

        // Check soft delete in DB
        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("SELECT deleted_at FROM store.product_reviews WHERE id = @id", db);
        cmd.Parameters.AddWithValue("id", created.Id);
        var deletedAt = await cmd.ExecuteScalarAsync();
        Assert.NotNull(deletedAt);
        Assert.False(deletedAt is DBNull);

        // My reviews should not list deleted review
        var myReviews = await service.GetMyReviewsAsync(ownerId);
        Assert.DoesNotContain(myReviews, r => r.Id == created.Id);

        // Eligibility should become true again!
        var eligibility = await service.GetEligibilityAsync(ownerId, productId);
        Assert.True(eligibility.Eligible);

        // Can create a brand new review now
        var newReview = await service.CreateReviewAsync(ownerId, productId, new CreateProductReviewRequest
        {
            Rating = 4,
            Content = "Đánh giá mới sau khi đã xóa đánh giá cũ."
        });
        Assert.NotEqual(created.Id, newReview.Id);
    }

    [Fact]
    public async Task ConcurrentCreateReview_ExactlyOneSucceeds_AndDatabaseHasOneActiveReview()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);
        await CreateDeliveredOrderAsync(customerId, skuId);

        var app = fixture.CreateConcurrentApplication();

        var task1 = Task.Run(async () =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();
            return await service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
            {
                Rating = 5,
                Content = "Đánh giá từ thread concurrent thứ nhất."
            });
        });

        var task2 = Task.Run(async () =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();
            return await service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
            {
                Rating = 4,
                Content = "Đánh giá từ thread concurrent thứ hai."
            });
        });

        var results = await Task.WhenAll(
            task1.ContinueWith(t => (Success: t.IsCompletedSuccessfully, Ex: t.Exception?.InnerException)),
            task2.ContinueWith(t => (Success: t.IsCompletedSuccessfully, Ex: t.Exception?.InnerException)));

        var successCount = results.Count(r => r.Success);
        var failCount = results.Count(r => !r.Success && r.Ex is ProductReviewFailure { Code: "REVIEW_ALREADY_EXISTS" });

        Assert.Equal(1, successCount);
        Assert.Equal(1, failCount);

        // Verify only 1 active review exists in DB
        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM store.product_reviews WHERE customer_id = @customerId AND product_id = @productId AND deleted_at IS NULL", db);
        cmd.Parameters.AddWithValue("customerId", customerId);
        cmd.Parameters.AddWithValue("productId", productId);

        var activeCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, activeCount);
    }

    [Fact]
    public async Task GetPublicReviews_And_Summary_CalculateCorrectly()
    {
        var (cust1, name1) = await CreateCustomerAsync("Trần Văn An");
        var (cust2, name2) = await CreateCustomerAsync("Lê Thị Bình");
        var (cust3, name3) = await CreateCustomerAsync("Phạm Quốc Cường");
        var productId = await CreateProductAsync("Giày cầu lông Yonex Eclipsion");
        var skuId = await CreateSkuAsync(productId);

        await CreateDeliveredOrderAsync(cust1, skuId);
        await CreateDeliveredOrderAsync(cust2, skuId);
        await CreateDeliveredOrderAsync(cust3, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        // cust1 gives 5 stars
        await service.CreateReviewAsync(cust1, productId, new CreateProductReviewRequest
        {
            Rating = 5,
            Content = "Giày êm, bám sân rất tốt, di chuyển không bị trượt."
        });

        // cust2 gives 4 stars
        await service.CreateReviewAsync(cust2, productId, new CreateProductReviewRequest
        {
            Rating = 4,
            Content = "Giày đẹp, đúng size, giao hàng đóng gói cẩn thận."
        });

        // cust3 gives 5 stars
        await service.CreateReviewAsync(cust3, productId, new CreateProductReviewRequest
        {
            Rating = 5,
            Content = "Rất hài lòng với chất lượng của đôi giày này!"
        });

        // Fetch summary
        var summary = await service.GetProductRatingSummaryAsync(productId);
        // Average: (5 + 4 + 5) / 3 = 14 / 3 = 4.67
        Assert.Equal(3, summary.ReviewCount);
        Assert.Equal(4.67m, summary.AverageRating);
        Assert.Equal(0, summary.Distribution.One);
        Assert.Equal(0, summary.Distribution.Two);
        Assert.Equal(0, summary.Distribution.Three);
        Assert.Equal(1, summary.Distribution.Four);
        Assert.Equal(2, summary.Distribution.Five);

        // Fetch public reviews
        var page = await service.GetPublicReviewsAsync(productId, new ReviewQuery { Page = 1, PageSize = 10, Sort = "highest" });
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(summary.AverageRating, page.Summary.AverageRating);

        var filtered = await service.GetPublicReviewsAsync(productId, new ReviewQuery { Rating = 5, PageSize = 1 });
        Assert.Equal(2, filtered.TotalCount);
        Assert.Equal(3, filtered.Summary.ReviewCount);
        Assert.Equal(5, Assert.Single(filtered.Items).Rating);
        var next = await service.GetPublicReviewsAsync(productId, new ReviewQuery { Rating = 5, PageSize = 1, Page = 2 });
        Assert.NotEqual(filtered.Items[0].Id, Assert.Single(next.Items).Id);
        var empty = await service.GetPublicReviewsAsync(productId, new ReviewQuery { Rating = 1 });
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
        Assert.Equal(3, empty.Summary.ReviewCount);
        var extreme = await service.GetPublicReviewsAsync(productId, new ReviewQuery { Page = int.MaxValue, PageSize = 100 });
        Assert.Empty(extreme.Items);
        Assert.Equal(100_000, extreme.Page);

        // Check masked display names
        foreach (var item in page.Items)
        {
            Assert.True(item.VerifiedPurchase);
            Assert.Contains("*", item.ReviewerDisplayName);
            Assert.DoesNotContain("@", item.ReviewerDisplayName);
        }
    }

    [Fact]
    public async Task ModerateReview_HideAndRestore_WithAuditLogs()
    {
        var (customerId, _) = await CreateCustomerAsync();
        var (moderatorId, modName) = await CreateModeratorAsync("STAFF");
        var productId = await CreateProductAsync();
        var skuId = await CreateSkuAsync(productId);
        await CreateDeliveredOrderAsync(customerId, skuId);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductReviewService>();

        var review = await service.CreateReviewAsync(customerId, productId, new CreateProductReviewRequest
        {
            Rating = 1,
            Content = "Nội dung nhận xét vi phạm tiêu chuẩn spam link lạ."
        });

        // 1. Moderate HIDE
        var hidden = await service.ModerateReviewAsync(moderatorId, "STAFF", review.Id, new ModerateProductReviewRequest
        {
            Action = "HIDE",
            Reason = "Vi phạm chính sách: chứa nội dung spam quảng cáo."
        }, "corr-mod-1");

        Assert.Equal("HIDDEN", hidden.ReviewStatus);
        Assert.Equal(moderatorId, hidden.ModeratedBy);
        Assert.Equal(modName, hidden.ModeratorName);
        Assert.Equal("Vi phạm chính sách: chứa nội dung spam quảng cáo.", hidden.ModerationReason);
        Assert.NotNull(hidden.ModeratedAt);

        var editedHidden = await service.UpdateReviewAsync(customerId, review.Id,
            new UpdateProductReviewRequest { Rating = 1, Content = "Nội dung đã chỉnh sửa nhưng vẫn cần kiểm duyệt." });
        Assert.Equal("HIDDEN", editedHidden.ReviewStatus);
        Assert.Equal(hidden.ModerationReason, editedHidden.ModerationReason);

        // Hidden review must NOT appear in public reviews
        var publicPage = await service.GetPublicReviewsAsync(productId, new ReviewQuery());
        Assert.Equal(0, publicPage.TotalCount);
        Assert.Empty(publicPage.Items);
        Assert.Equal(0, publicPage.Summary.ReviewCount);
        Assert.Equal(0.00m, publicPage.Summary.AverageRating);

        // 2. Moderate RESTORE
        var restored = await service.ModerateReviewAsync(moderatorId, "STAFF", review.Id, new ModerateProductReviewRequest
        {
            Action = "RESTORE",
            Reason = "Đã kiểm tra lại và nội dung hợp lệ."
        }, "corr-mod-2");

        Assert.Equal("PUBLISHED", restored.ReviewStatus);
        Assert.Null(restored.ModeratedBy);
        Assert.Null(restored.ModeratorName);
        Assert.Null(restored.ModerationReason);
        Assert.Null(restored.ModeratedAt);

        // Restored review should now appear in public reviews and summary
        var restoredPage = await service.GetPublicReviewsAsync(productId, new ReviewQuery());
        Assert.Equal(1, restoredPage.TotalCount);
        Assert.Single(restoredPage.Items);
        Assert.Equal(1, restoredPage.Summary.ReviewCount);
        Assert.Equal(1.00m, restoredPage.Summary.AverageRating);

        // Verify Audit Logs written in DB
        await using var db = await fixture.OpenDatabaseAsync();
        await using var auditCmd = new NpgsqlCommand(
            "SELECT action, entity_type, entity_id, correlation_id FROM store.audit_logs WHERE entity_id = @id ORDER BY occurred_at ASC", db);
        auditCmd.Parameters.AddWithValue("id", review.Id);

        await using var reader = await auditCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("PRODUCT_REVIEW_HIDDEN", reader.GetString(0));
        Assert.Equal("ProductReview", reader.GetString(1));
        Assert.Equal("corr-mod-1", reader.GetString(3));

        Assert.True(await reader.ReadAsync());
        Assert.Equal("PRODUCT_REVIEW_RESTORED", reader.GetString(0));
        Assert.Equal("ProductReview", reader.GetString(1));
        Assert.Equal("corr-mod-2", reader.GetString(3));
    }

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
        var email = $"api_cust_{Guid.NewGuid():N}@example.invalid";
        using var reg = await Send(client, "auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Api Customer" });
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
        var email = $"api_{roleKey.ToLowerInvariant()}_{Guid.NewGuid():N}@example.invalid";
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
        insertCmd.Parameters.AddWithValue("name", $"{roleKey} Reviewer");
        await insertCmd.ExecuteNonQueryAsync();

        var client = fixture.CreateClient();
        using var login = await Send(client, "auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, id);
    }

    [Fact]
    public async Task PublicCatalog_Reviews_Endpoint_IsAccessibleWithoutAuthentication()
    {
        var productId = await CreateProductAsync();
        using var guest = fixture.CreateClient();

        using var response = await guest.GetAsync($"/api/v1/catalog/products/{productId}/reviews");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("items", out var items));
        Assert.True(body.TryGetProperty("summary", out var summary));
        Assert.Equal(0, summary.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task RBAC_CustomerAndModerationEndpoints_RequireCorrectRoles()
    {
        var productId = await CreateProductAsync();
        using var guest = fixture.CreateClient();

        // 1. Guest cannot access Customer protected endpoints
        var guestElig = await guest.GetAsync($"/api/v1/products/{productId}/review-eligibility");
        Assert.Equal(HttpStatusCode.Unauthorized, guestElig.StatusCode);

        var guestPost = await Send(guest, $"products/{productId}/reviews", new { rating = 5, content = "Nội dung nhận xét hợp lệ dài." });
        Assert.Equal(HttpStatusCode.Unauthorized, guestPost.StatusCode);

        // 2. Staff cannot access Customer review creation
        var (staffClient, _) = await CreateAuthenticatedRoleClient("STAFF");
        using (staffClient)
        {
            var staffPost = await Send(staffClient, $"products/{productId}/reviews", new { rating = 5, content = "Staff cố tạo review." });
            Assert.Equal(HttpStatusCode.Forbidden, staffPost.StatusCode);

            // Staff CAN access moderation endpoint
            var staffMod = await staffClient.GetAsync("/api/v1/moderation/reviews");
            Assert.Equal(HttpStatusCode.OK, staffMod.StatusCode);
        }

        // 3. Admin CAN access moderation endpoint
        var (adminClient, _) = await CreateAuthenticatedRoleClient("ADMIN");
        using (adminClient)
        {
            var adminMod = await adminClient.GetAsync("/api/v1/moderation/reviews");
            Assert.Equal(HttpStatusCode.OK, adminMod.StatusCode);
        }

        // 4. Customer CANNOT access moderation endpoint
        var (custClient, _) = await CreateAuthenticatedCustomerClient();
        using (custClient)
        {
            var custMod = await custClient.GetAsync("/api/v1/moderation/reviews");
            Assert.Equal(HttpStatusCode.Forbidden, custMod.StatusCode);
        }
    }

    [Fact]
    public async Task Customer_FullHttpLifecycle_CreateUpdateDeleteReviews()
    {
        var (custClient, custId) = await CreateAuthenticatedCustomerClient();
        using (custClient)
        {
            var productId = await CreateProductAsync("Vợt Cầu Lông Yonex HTTP Test");
            var skuId = await CreateSkuAsync(productId);
            await CreateDeliveredOrderAsync(custId, skuId);

            // 1. Check eligibility via HTTP
            using var eligResp = await custClient.GetAsync($"/api/v1/products/{productId}/review-eligibility");
            Assert.Equal(HttpStatusCode.OK, eligResp.StatusCode);
            var elig = await eligResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(elig.GetProperty("eligible").GetBoolean());

            // 2. Create review via HTTP POST
            using var createResp = await Send(custClient, $"products/{productId}/reviews", new
            {
                rating = 5,
                content = "Đánh giá xuất sắc qua giao thức HTTP API!"
            });
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            var reviewId = Guid.Parse(created.GetProperty("id").GetString()!);
            Assert.Equal(5, created.GetProperty("rating").GetInt16());
            Assert.Equal("PUBLISHED", created.GetProperty("reviewStatus").GetString());

            // 3. Get my reviews via HTTP GET
            using var myResp = await custClient.GetAsync("/api/v1/reviews/mine");
            Assert.Equal(HttpStatusCode.OK, myResp.StatusCode);
            var myReviews = await myResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(myReviews.GetArrayLength() >= 1);

            // 4. Update review via HTTP PATCH
            using var updateResp = await Send(custClient, $"reviews/{reviewId}", new
            {
                rating = 4,
                content = "Cập nhật lại đánh giá thành 4 sao qua HTTP PATCH."
            }, HttpMethod.Patch);
            Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
            var updated = await updateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(4, updated.GetProperty("rating").GetInt16());

            // 5. Staff moderates review via HTTP PATCH
            var (staffClient, _) = await CreateAuthenticatedRoleClient("STAFF");
            using (staffClient)
            {
                using var hideResp = await Send(staffClient, $"moderation/reviews/{reviewId}", new
                {
                    action = "HIDE",
                    reason = "Ẩn tạm thời để kiểm tra chính sách qua HTTP."
                }, HttpMethod.Patch);
                Assert.Equal(HttpStatusCode.OK, hideResp.StatusCode);
                var hidden = await hideResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("HIDDEN", hidden.GetProperty("reviewStatus").GetString());
            }

            // 6. Delete review via HTTP DELETE
            using var deleteResp = await Send(custClient, $"reviews/{reviewId}", method: HttpMethod.Delete);
            Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);
        }
    }

    [Fact]
    public async Task Catalog_Endpoints_Return_AverageRating_And_ReviewCount()
    {
        var (cust, custId) = await CreateAuthenticatedCustomerClient();
        using (cust)
        {
            var productId = await CreateProductAsync("Vợt Yonex Duora 10 LT");
            var skuId = await CreateSkuAsync(productId);
            await CreateDeliveredOrderAsync(custId, skuId);

            // Create review
            using var create = await Send(cust, $"products/{productId}/reviews", new
            {
                rating = 5,
                content = "Vợt kiểm soát cầu cực kỳ chính xác và linh hoạt."
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            // 1. Check Product Detail
            using var guest = fixture.CreateClient();
            using var detailResp = await guest.GetAsync($"/api/v1/catalog/products/{productId}");
            Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
            var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5.00m, detail.GetProperty("averageRating").GetDecimal());
            Assert.Equal(1, detail.GetProperty("reviewCount").GetInt32());

            // 2. Check Product List
            using var listResp = await guest.GetAsync($"/api/v1/catalog/products?q=Duora");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            var items = list.GetProperty("items");
            Assert.True(items.GetArrayLength() >= 1);
            var item = items[0];
            Assert.Equal(5.00m, item.GetProperty("averageRating").GetDecimal());
            Assert.Equal(1, item.GetProperty("reviewCount").GetInt32());
        }
    }
}
