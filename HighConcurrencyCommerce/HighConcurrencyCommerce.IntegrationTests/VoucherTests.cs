using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Checkout;
using HighConcurrencyCommerce.Application.CustomerOrders;
using HighConcurrencyCommerce.Application.Fulfillment;
using HighConcurrencyCommerce.Application.Orders;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class VoucherTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string StrongPassword = "A secure test pass 123!@#";

    private static string Email(string prefix) => $"{prefix}_{Guid.NewGuid():N}@example.com";

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, HttpMethod? method = null, bool includeCsrf = true)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/" + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (includeCsrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        }
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid Id, string Email)> CreateUserWithRoleAsync(string roleKey)
    {
        var client = fixture.CreateClient();
        var email = Email(roleKey.ToLowerInvariant());
        using var reg = await Send(client, "auth/register", new
        {
            email,
            password = StrongPassword,
            confirmPassword = StrongPassword,
            fullName = $"{roleKey} User"
        });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);

        await using var db = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE store.user_accounts SET role_id = (SELECT id FROM store.roles WHERE role_key = @role), email_verified_at = now() WHERE lower(email) = lower(@email) RETURNING id", db);
        cmd.Parameters.AddWithValue("role", roleKey);
        cmd.Parameters.AddWithValue("email", email);
        var id = (Guid)(await cmd.ExecuteScalarAsync())!;

        using var login = await Send(client, "auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (client, id, email);
    }

    [Fact]
    public async Task Migration010_CanBeAppliedMultipleTimesWithoutErrorAndPreservesData()
    {
        await using var connection = await fixture.OpenDatabaseAsync();

        // 1. Read embedded migration resource
        await using var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream("BasicVouchersSchema.sql")
            ?? throw new InvalidOperationException("Missing BasicVouchersSchema.sql");
        using var reader = new StreamReader(stream);
        var migrationSql = await reader.ReadToEndAsync();

        // 2. Insert a test voucher via EF Core
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var admin = await db.UserAccounts.Include(u => u.Role).FirstAsync(u => u.Role.RoleKey == "ADMIN");

        var testVoucher = new Voucher
        {
            Id = Guid.NewGuid(),
            Code = $"TESTMIG_{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            Name = "Legacy voucher name",
            VoucherStatus = "DRAFT",
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MaxDiscountAmount = null,
            MinimumOrderAmount = 200000m,
            TotalUsageLimit = 100,
            PerCustomerLimit = 1,
            UsageHeld = 0,
            UsageConsumed = 0,
            BudgetTotal = 5000000m,
            BudgetHeld = 0m,
            BudgetConsumed = 0m,
            StartsAt = DateTime.UtcNow.AddDays(1),
            EndsAt = DateTime.UtcNow.AddDays(10),
            CreatedBy = admin.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Vouchers.Add(testVoucher);
        await db.SaveChangesAsync();

        // 3. Simulate an installation created by the first T38 revision, before
        // the program-name column existed, then apply the additive upgrade.
        await using (var legacySchema = new NpgsqlCommand(
            "ALTER TABLE store.vouchers DROP COLUMN name", connection))
        {
            await legacySchema.ExecuteNonQueryAsync();
        }

        // 4. Re-apply migration twice to verify both upgrade and idempotency.
        for (var i = 0; i < 2; i++)
        {
            await using var cmd = new NpgsqlCommand(migrationSql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        // 5. Existing rows are backfilled with their code and protected by the
        // database invariant instead of retaining an empty display name.
        using var verifyScope = fixture.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var loaded = await verifyDb.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == testVoucher.Id);
        Assert.NotNull(loaded);
        Assert.Equal(testVoucher.Code, loaded.Code);
        Assert.Equal(testVoucher.Code, loaded.Name);
        Assert.Equal(testVoucher.DiscountValue, loaded.DiscountValue);
        Assert.Equal(testVoucher.MinimumOrderAmount, loaded.MinimumOrderAmount);

        await using var blankName = new NpgsqlCommand(
            "UPDATE store.vouchers SET name = '   ' WHERE id = @id", connection);
        blankName.Parameters.AddWithValue("id", testVoucher.Id);
        var constraintError = await Assert.ThrowsAsync<PostgresException>(() => blankName.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, constraintError.SqlState);
    }

    [Fact]
    public async Task DbContext_VoucherAndRedemptionMappings_AreValid()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();

        var voucherCount = await db.Vouchers.CountAsync();
        var redemptionCount = await db.VoucherRedemptions.CountAsync();
        Assert.True(voucherCount >= 0);
        Assert.True(redemptionCount >= 0);

        var orderSubtotalQuery = await db.SalesOrders
            .Select(o => new { o.Id, o.TotalAmount, o.SubtotalAmount, o.VoucherDiscountAmount, o.VoucherCodeSnapshot })
            .Take(1)
            .ToListAsync();
        Assert.NotNull(orderSubtotalQuery);
    }

    [Fact]
    public async Task RBAC_Guest_Customer_Staff_are_blocked_from_admin_vouchers_and_Admin_can_access()
    {
        var guestClient = fixture.CreateClient();
        var (customerClient, _, _) = await CreateUserWithRoleAsync("CUSTOMER");
        var (staffClient, _, _) = await CreateUserWithRoleAsync("STAFF");
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");

        // 1. Guest -> 401
        using var guestRes = await guestClient.GetAsync("/api/v1/admin/vouchers");
        Assert.Equal(HttpStatusCode.Unauthorized, guestRes.StatusCode);

        // 2. Customer -> 403
        using var custRes = await customerClient.GetAsync("/api/v1/admin/vouchers");
        Assert.Equal(HttpStatusCode.Forbidden, custRes.StatusCode);

        // 3. Staff -> 403
        using var staffRes = await staffClient.GetAsync("/api/v1/admin/vouchers");
        Assert.Equal(HttpStatusCode.Forbidden, staffRes.StatusCode);

        // 4. Admin -> 200
        using var adminRes = await adminClient.GetAsync("/api/v1/admin/vouchers");
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
    }

    [Fact]
    public async Task AdminVoucher_Create_NormalizesCode_And_RejectsDuplicates()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var codeSuffix = Guid.NewGuid().ToString("N")[..8];
        var rawCode = $"  vch_{codeSuffix}  ";
        var expectedNormalized = $"VCH_{codeSuffix}".ToUpperInvariant();

        var createReq = new CreateVoucherRequest
        {
            Code = rawCode,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 30000m,
            MaxDiscountAmount = null,
            MinimumOrderAmount = 150000m,
            TotalUsageLimit = 50,
            PerCustomerLimit = 1,
            BudgetTotal = 1500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };

        // 1. Create with mixed case and leading/trailing spaces
        using var createRes = await Send(adminClient, "admin/vouchers", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(created);
        Assert.Equal(expectedNormalized, created.Code);
        Assert.Equal("DRAFT", created.VoucherStatus);

        // 2. Creating duplicate with different case -> 409 Conflict
        var dupReq = new CreateVoucherRequest
        {
            Code = expectedNormalized.ToLowerInvariant(),
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 30000m,
            MaxDiscountAmount = null,
            MinimumOrderAmount = 150000m,
            TotalUsageLimit = 50,
            PerCustomerLimit = 1,
            BudgetTotal = 1500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        using var dupRes = await Send(adminClient, "admin/vouchers", dupReq);
        Assert.Equal(HttpStatusCode.Conflict, dupRes.StatusCode);
        var problem = await dupRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONFLICT", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AdminVoucher_ConcurrentDuplicateCreate_ReturnsOne201AndOne409_No500()
    {
        var (adminClient1, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (adminClient2, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var code = $"CONC_{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        var req1 = new CreateVoucherRequest
        {
            Code = code,
            Name = "Voucher Đồng Thời 1",
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 30000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 50,
            PerCustomerLimit = 1,
            BudgetTotal = 1500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };

        var req2 = new CreateVoucherRequest
        {
            Code = code.ToLowerInvariant(),
            Name = "Voucher Đồng Thời 2",
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 30000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 50,
            PerCustomerLimit = 1,
            BudgetTotal = 1500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };

        var task1 = Send(adminClient1, "admin/vouchers", req1);
        var task2 = Send(adminClient2, "admin/vouchers", req2);

        var responses = await Task.WhenAll(task1, task2);
        var statusCodes = responses.Select(r => r.StatusCode).ToList();

        // Exactly one Created (201) and one Conflict (409), neither 500
        Assert.Contains(HttpStatusCode.Created, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statusCodes);

        foreach (var r in responses)
            r.Dispose();
    }

    [Fact]
    public async Task AdminVoucher_NamePersistenceAndSearch_WorksEndToEnd()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var code = $"NAME_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var uniqueName = $"Ưu đãi Cầu Lông {Guid.NewGuid():N}"[..25];

        var createReq = new CreateVoucherRequest
        {
            Code = code,
            Name = uniqueName,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 200000m,
            TotalUsageLimit = 100,
            PerCustomerLimit = 1,
            BudgetTotal = 5000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };

        using var createRes = await Send(adminClient, "admin/vouchers", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(created);
        Assert.Equal(uniqueName, created.Name);

        // Search by unique name
        using var searchByNameRes = await adminClient.GetAsync($"api/v1/admin/vouchers?q={Uri.EscapeDataString(uniqueName[..10])}");
        Assert.Equal(HttpStatusCode.OK, searchByNameRes.StatusCode);
        var searchResult = await searchByNameRes.Content.ReadFromJsonAsync<AdminVoucherPageDto>();
        Assert.NotNull(searchResult);
        Assert.Contains(searchResult.Items, v => v.Code == code && v.Name == uniqueName);

        // Update name
        var updatedName = $"Tên Mới {Guid.NewGuid():N}"[..20];
        var updateReq = new UpdateVoucherRequest
        {
            Code = code,
            Name = updatedName,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 200000m,
            TotalUsageLimit = 100,
            PerCustomerLimit = 1,
            BudgetTotal = 5000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };

        using var updateRes = await Send(adminClient, $"admin/vouchers/{created.Id}", updateReq, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(updated);
        Assert.Equal(updatedName, updated.Name);
    }

    [Fact]
    public async Task AdminVoucher_ExtremePagination_Returns400OrEmptyPage_No500()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");

        // 1. Extreme page number exceeding Range validation -> 400 Bad Request
        using var extremePageRes = await adminClient.GetAsync("api/v1/admin/vouchers?page=2147483647&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, extremePageRes.StatusCode);

        // 2. PageSize exceeding Range validation -> 400 Bad Request
        using var extremeSizeRes = await adminClient.GetAsync("api/v1/admin/vouchers?page=1&pageSize=999999");
        Assert.Equal(HttpStatusCode.BadRequest, extremeSizeRes.StatusCode);

        // 3. Valid high page number within Range but beyond total count -> 200 OK with empty items
        using var validHighPageRes = await adminClient.GetAsync("api/v1/admin/vouchers?page=100000&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, validHighPageRes.StatusCode);
        var pageResult = await validHighPageRes.Content.ReadFromJsonAsync<AdminVoucherPageDto>();
        Assert.NotNull(pageResult);
        Assert.Empty(pageResult.Items);
    }

    [Fact]
    public async Task AdminVoucher_Validation_RejectsInvalidPayloads()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");

        // 1. Invalid code: special characters
        using var res1 = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = "INVALID$$$",
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 10000m,
            BudgetTotal = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

        // 2. FIXED_AMOUNT with MaxDiscountAmount -> 400
        using var res2 = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = $"FIXED_ERR_{Guid.NewGuid():N}"[..12],
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 10000m,
            MaxDiscountAmount = 50000m,
            BudgetTotal = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);

        // 3. PERCENTAGE without MaxDiscountAmount -> 400
        using var res3 = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = $"PCT_ERR_{Guid.NewGuid():N}"[..12],
            DiscountType = "PERCENTAGE",
            DiscountValue = 10m,
            MaxDiscountAmount = null,
            BudgetTotal = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.BadRequest, res3.StatusCode);

        // 4. StartsAt >= EndsAt -> 400
        using var res4 = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = $"TIME_ERR_{Guid.NewGuid():N}"[..12],
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 10000m,
            BudgetTotal = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow.AddDays(2),
            EndsAt = DateTime.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.BadRequest, res4.StatusCode);

        // 5. PerCustomerLimit > TotalUsageLimit -> 400
        using var res5 = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = $"LIMIT_ERR_{Guid.NewGuid():N}"[..12],
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 10000m,
            BudgetTotal = 100000m,
            TotalUsageLimit = 5,
            PerCustomerLimit = 10,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.BadRequest, res5.StatusCode);
    }

    [Fact]
    public async Task AdminVoucher_Lifecycle_Draft_Update_Activate_Pause_Resume_End_WithAuditLog()
    {
        var (adminClient, adminId, _) = await CreateUserWithRoleAsync("ADMIN");
        var code = $"LIFE_{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        // 1. Create DRAFT
        var createReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "PERCENTAGE",
            DiscountValue = 15m,
            MaxDiscountAmount = 100000m,
            MinimumOrderAmount = 300000m,
            TotalUsageLimit = 20,
            PerCustomerLimit = 2,
            BudgetTotal = 2000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(5)
        };
        using var createRes = await Send(adminClient, "admin/vouchers", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var voucher = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(voucher);
        Assert.Equal("DRAFT", voucher.VoucherStatus);

        // Verify AuditLog for VOUCHER_CREATED
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var audit = await db.AuditLogs.FirstOrDefaultAsync(a => a.EntityId == voucher.Id && a.Action == "VOUCHER_CREATED");
            Assert.NotNull(audit);
            Assert.Equal(adminId, audit.ActorId);
        }

        // 2. Update in DRAFT
        var updateReq = new UpdateVoucherRequest
        {
            Code = code,
            DiscountType = "PERCENTAGE",
            DiscountValue = 20m, // updated to 20%
            MaxDiscountAmount = 120000m,
            MinimumOrderAmount = 300000m,
            TotalUsageLimit = 30,
            PerCustomerLimit = 2,
            BudgetTotal = 3600000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(5)
        };
        using var updateRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}", updateReq, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(updated);
        Assert.Equal(20m, updated.DiscountValue);
        Assert.Equal(1, updated.Revision);

        // Verify AuditLog for VOUCHER_UPDATED
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var audit = await db.AuditLogs.FirstOrDefaultAsync(a => a.EntityId == voucher.Id && a.Action == "VOUCHER_UPDATED");
            Assert.NotNull(audit);
        }

        // 3. Activate -> ACTIVE
        using var actRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}/activate");
        Assert.Equal(HttpStatusCode.OK, actRes.StatusCode);
        var activated = await actRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(activated);
        Assert.Equal("ACTIVE", activated.VoucherStatus);
        Assert.NotNull(activated.ActivatedAt);
        Assert.Equal(adminId, activated.ActivatedBy);

        // Verify AuditLog for VOUCHER_ACTIVATED
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var audit = await db.AuditLogs.FirstOrDefaultAsync(a => a.EntityId == voucher.Id && a.Action == "VOUCHER_ACTIVATED");
            Assert.NotNull(audit);
        }

        // 4. Update in ACTIVE -> 409 INVALID_STATE (immutable after activate!)
        using var immutRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}", updateReq, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.Conflict, immutRes.StatusCode);
        var immutProblem = await immutRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_STATE", immutProblem.GetProperty("code").GetString());

        // 5. Pause -> PAUSED
        using var pauseRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}/pause");
        Assert.Equal(HttpStatusCode.OK, pauseRes.StatusCode);
        var paused = await pauseRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(paused);
        Assert.Equal("PAUSED", paused.VoucherStatus);

        // 6. Resume -> ACTIVE
        using var resumeRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}/resume");
        Assert.Equal(HttpStatusCode.OK, resumeRes.StatusCode);
        var resumed = await resumeRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(resumed);
        Assert.Equal("ACTIVE", resumed.VoucherStatus);

        // 7. End -> ENDED
        using var endRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}/end");
        Assert.Equal(HttpStatusCode.OK, endRes.StatusCode);
        var ended = await endRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(ended);
        Assert.Equal("ENDED", ended.VoucherStatus);

        // 8. End on ENDED -> 409 INVALID_STATE
        using var endAgainRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}/end");
        Assert.Equal(HttpStatusCode.Conflict, endAgainRes.StatusCode);

        // 9. Activate on ENDED -> 409 INVALID_STATE
        using var actEndedRes = await Send(adminClient, $"admin/vouchers/{voucher.Id}/activate");
        Assert.Equal(HttpStatusCode.Conflict, actEndedRes.StatusCode);
    }

    [Fact]
    public async Task AdminVoucher_Query_SearchAndPagination_AndNoStoreCache()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var uniquePrefix = $"QRY_{Guid.NewGuid():N}"[..10].ToUpperInvariant();

        // Create 2 vouchers with unique prefix
        for (var i = 1; i <= 2; i++)
        {
            var req = new CreateVoucherRequest
            {
                Code = $"{uniquePrefix}_{i}",
                DiscountType = "FIXED_AMOUNT",
                DiscountValue = 10000m * i,
                BudgetTotal = 500000m,
                TotalUsageLimit = 20,
                PerCustomerLimit = 1,
                StartsAt = DateTime.UtcNow,
                EndsAt = DateTime.UtcNow.AddDays(7)
            };
            using var res = await Send(adminClient, "admin/vouchers", req);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        }

        // Query by Q
        using var qRes = await adminClient.GetAsync($"/api/v1/admin/vouchers?q={uniquePrefix}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, qRes.StatusCode);
        Assert.True(qRes.Headers.CacheControl?.NoStore);

        var pageData = await qRes.Content.ReadFromJsonAsync<AdminVoucherPageDto>();
        Assert.NotNull(pageData);
        Assert.True(pageData.TotalCount >= 2);
        Assert.All(pageData.Items.Where(x => x.Code.StartsWith(uniquePrefix)), item =>
        {
            Assert.Contains(uniquePrefix, item.Code);
            Assert.Equal("DRAFT", item.VoucherStatus);
        });
    }

    [Fact]
    public async Task Customer_Preview_RBAC_Guest_Staff_Admin_blocked_Customer_allowed()
    {
        var guestClient = fixture.CreateClient();
        var (staffClient, _, _) = await CreateUserWithRoleAsync("STAFF");
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, _, _) = await CreateUserWithRoleAsync("CUSTOMER");

        var payload = new { code = "ANY_CODE" };

        // 1. Guest -> 401
        using var guestRes = await Send(guestClient, "vouchers/preview", payload, includeCsrf: false);
        Assert.Equal(HttpStatusCode.Unauthorized, guestRes.StatusCode);

        // 2. Staff -> 403
        using var staffRes = await Send(staffClient, "vouchers/preview", payload);
        Assert.Equal(HttpStatusCode.Forbidden, staffRes.StatusCode);

        // 3. Admin -> 403
        using var adminRes = await Send(adminClient, "vouchers/preview", payload);
        Assert.Equal(HttpStatusCode.Forbidden, adminRes.StatusCode);

        // 4. Customer without CSRF -> 400 with CSRF_INVALID
        using var noCsrfRes = await Send(customerClient, "vouchers/preview", payload, includeCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrfRes.StatusCode);
        var csrfProblem = await noCsrfRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CSRF_INVALID", csrfProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Customer_Preview_FixedAmount_ReturnsExpectedDiscount()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, _, _) = await CreateUserWithRoleAsync("CUSTOMER");

        var code = $"FIX_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 150000m,
            TotalUsageLimit = 100,
            PerCustomerLimit = 2,
            BudgetTotal = 5000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        using var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(created);

        using var actRes = await Send(adminClient, $"admin/vouchers/{created.Id}/activate");
        Assert.Equal(HttpStatusCode.OK, actRes.StatusCode);

        // Customer puts 2 items of SKU 9 (100,000 VND each = 200,000 VND)
        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        using var addRes = await Send(customerClient, $"cart/items/{sku9}", new { quantity = 2 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, addRes.StatusCode);

        // Customer previews code with lowercase
        using var previewRes = await Send(customerClient, "vouchers/preview", new { code = code.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, previewRes.StatusCode);
        var preview = await previewRes.Content.ReadFromJsonAsync<VoucherPreviewDto>();
        Assert.NotNull(preview);
        Assert.Equal(code, preview.Code);
        Assert.Equal(200000m, preview.SubtotalAmount);
        Assert.Equal(50000m, preview.DiscountAmount);
        Assert.Equal(150000m, preview.TotalAmount);
        Assert.True(preview.ValidUntil > DateTime.UtcNow);
    }

    [Fact]
    public async Task Customer_Preview_Percentage_FloorAndCap_AppliedCorrectly()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, _, _) = await CreateUserWithRoleAsync("CUSTOMER");

        var code = $"PCT_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "PERCENTAGE",
            DiscountValue = 15m, // 15%
            MaxDiscountAmount = 40000m, // max cap 40,000 VND
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 50,
            PerCustomerLimit = 1,
            BudgetTotal = 2000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        using var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        Assert.NotNull(created);

        using var actRes = await Send(adminClient, $"admin/vouchers/{created.Id}/activate");
        Assert.Equal(HttpStatusCode.OK, actRes.StatusCode);

        // Scenario 1: Below cap. Subtotal = 2 * 100,000 = 200,000 VND. 15% = 30,000 VND (< 40,000 cap)
        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        using var addRes1 = await Send(customerClient, $"cart/items/{sku9}", new { quantity = 2 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, addRes1.StatusCode);

        using var previewRes1 = await Send(customerClient, "vouchers/preview", new { code });
        Assert.Equal(HttpStatusCode.OK, previewRes1.StatusCode);
        var preview1 = await previewRes1.Content.ReadFromJsonAsync<VoucherPreviewDto>();
        Assert.NotNull(preview1);
        Assert.Equal(200000m, preview1.SubtotalAmount);
        Assert.Equal(30000m, preview1.DiscountAmount);
        Assert.Equal(170000m, preview1.TotalAmount);

        // Scenario 2: Exceeding cap. Add SKU 2 (3,500,000 VND). Subtotal = 3,700,000 VND. 15% = 555,000 > 40,000 cap
        var sku2 = Guid.Parse("30000000-0000-0000-0000-000000000002");
        using var addRes2 = await Send(customerClient, $"cart/items/{sku2}", new { quantity = 1 }, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, addRes2.StatusCode);

        using var previewRes2 = await Send(customerClient, "vouchers/preview", new { code });
        Assert.Equal(HttpStatusCode.OK, previewRes2.StatusCode);
        var preview2 = await previewRes2.Content.ReadFromJsonAsync<VoucherPreviewDto>();
        Assert.NotNull(preview2);
        Assert.Equal(3700000m, preview2.SubtotalAmount);
        Assert.Equal(40000m, preview2.DiscountAmount);
        Assert.Equal(3660000m, preview2.TotalAmount);
    }

    [Fact]
    public async Task Customer_Preview_Rejections_And_ReadOnly_Verified()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, customerId, _) = await CreateUserWithRoleAsync("CUSTOMER");

        // 1. Empty cart -> 400 VALIDATION_ERROR
        using var emptyCartRes = await Send(customerClient, "vouchers/preview", new { code = "ANY_CODE" });
        Assert.Equal(HttpStatusCode.BadRequest, emptyCartRes.StatusCode);
        var emptyProblem = await emptyCartRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", emptyProblem.GetProperty("code").GetString());

        // Put 1 item of SKU 9 (100,000 VND)
        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        await Send(customerClient, $"cart/items/{sku9}", new { quantity = 1 }, HttpMethod.Put);

        // 2. Non-existent code -> 404 VOUCHER_NOT_FOUND
        using var notFoundRes = await Send(customerClient, "vouchers/preview", new { code = "NON_EXISTENT_VCH" });
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // 3. Draft voucher -> 404 VOUCHER_NOT_FOUND
        var draftCode = $"DFT_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = draftCode,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 20000m,
            BudgetTotal = 500000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        using var draftRes = await Send(customerClient, "vouchers/preview", new { code = draftCode });
        Assert.Equal(HttpStatusCode.NotFound, draftRes.StatusCode);

        // 4. Paused voucher -> 409 VOUCHER_NOT_ACTIVE
        var pauseCode = $"PSD_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var createPauseRes = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = pauseCode,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 20000m,
            BudgetTotal = 500000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        var pauseVch = await createPauseRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{pauseVch!.Id}/activate");
        await Send(adminClient, $"admin/vouchers/{pauseVch.Id}/pause");

        using var pauseRes = await Send(customerClient, "vouchers/preview", new { code = pauseCode });
        Assert.Equal(HttpStatusCode.Conflict, pauseRes.StatusCode);
        var pauseProblem = await pauseRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VOUCHER_NOT_ACTIVE", pauseProblem.GetProperty("code").GetString());

        // 5. Minimum order not met -> 409 VOUCHER_MINIMUM_NOT_MET
        var minCode = $"MIN_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var createMinRes = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = minCode,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 20000m,
            MinimumOrderAmount = 500000m, // Cart only has 100,000 VND
            BudgetTotal = 500000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        var minVch = await createMinRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{minVch!.Id}/activate");

        using var minRes = await Send(customerClient, "vouchers/preview", new { code = minCode });
        Assert.Equal(HttpStatusCode.Conflict, minRes.StatusCode);
        var minProblem = await minRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VOUCHER_MINIMUM_NOT_MET", minProblem.GetProperty("code").GetString());

        // 6. Verify READ-ONLY: Confirm that counters on pauseVch and minVch did NOT change
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == minVch.Id);
            Assert.Equal(0, checkedVoucher.UsageHeld);
            Assert.Equal(0, checkedVoucher.UsageConsumed);
            Assert.Equal(0m, checkedVoucher.BudgetHeld);
            Assert.Equal(0m, checkedVoucher.BudgetConsumed);

            var redemptionCount = await db.VoucherRedemptions.CountAsync(r => r.VoucherId == minVch.Id);
            Assert.Equal(0, redemptionCount);
        }
    }

    private async Task<(HttpClient Client, Guid CustomerId, Guid AddressId)> CreateCustomerWithAddressAsync()
    {
        var (client, customerId, _) = await CreateUserWithRoleAsync("CUSTOMER");
        using var addressResponse = await Send(client, "account/addresses", new
        {
            recipientName = "Nguyen Van Test",
            phone = "0901234567",
            addressLine = "123 Duong Le Loi",
            ward = "Phuong Ben Nghe",
            province = "TP Ho Chi Minh",
            isDefault = true
        });
        Assert.Equal(HttpStatusCode.Created, addressResponse.StatusCode);
        var body = await addressResponse.Content.ReadFromJsonAsync<JsonElement>();
        var addressId = Guid.Parse(body.GetProperty("id").GetString()!);
        return (client, customerId, addressId);
    }

    private static async Task<HttpResponseMessage> SendCheckout(
        HttpClient client,
        object payload,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkouts")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Checkout_WithVoucher_AllocatesHeld_UpdatesUsageAndBudget_AndSetsSnapshots()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, customerId, addressId) = await CreateCustomerWithAddressAsync();

        var code = $"CHK_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 150000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 2,
            BudgetTotal = 1000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        // SKU 9 (100,000 VND each, quantity = 2 -> subtotal = 200,000 VND)
        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        var checkoutPayload = new
        {
            voucherCode = code.ToLowerInvariant(),
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 2, flashSaleOfferId = (Guid?)null } }
        };

        var idemKey = $"idem-chk-{Guid.NewGuid():N}";
        using var res = await SendCheckout(customerClient, checkoutPayload, idemKey);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var result = await res.Content.ReadFromJsonAsync<CheckoutResultDto>();
        Assert.NotNull(result);
        Assert.Equal(200000m, result.SubtotalAmount);
        Assert.Equal(50000m, result.VoucherDiscountAmount);
        Assert.Equal(150000m, result.TotalAmount);
        Assert.Equal(code, result.VoucherCode);

        // Database assertions
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();

        var order = await db.SalesOrders.FirstAsync(o => o.Id == result.OrderId);
        Assert.Equal(200000m, order.SubtotalAmount);
        Assert.Equal(50000m, order.VoucherDiscountAmount);
        Assert.Equal(150000m, order.TotalAmount);
        Assert.Equal(code, order.VoucherCodeSnapshot);

        var payment = await db.Payments.FirstAsync(p => p.OrderId == result.OrderId);
        Assert.Equal(150000m, payment.ExpectedAmount);

        var updatedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
        Assert.Equal(1, updatedVoucher.UsageHeld);
        Assert.Equal(0, updatedVoucher.UsageConsumed);
        Assert.Equal(50000m, updatedVoucher.BudgetHeld);
        Assert.Equal(0m, updatedVoucher.BudgetConsumed);

        var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == result.OrderId);
        Assert.Equal(vch.Id, redemption.VoucherId);
        Assert.Equal(customerId, redemption.CustomerId);
        Assert.Equal(200000m, redemption.SubtotalSnapshot);
        Assert.Equal(50000m, redemption.DiscountAmount);
        Assert.Equal("HELD", redemption.RedemptionStatus);
    }

    [Fact]
    public async Task Checkout_Idempotency_SameKeyAndCode_ReplaysSuccess_WithoutDoubleAllocation()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, _, addressId) = await CreateCustomerWithAddressAsync();

        var code = $"IDEM_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var createRes = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 30000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 2,
            BudgetTotal = 1000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        var payload = new
        {
            voucherCode = $"  {code.ToLowerInvariant()}  ",
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };

        var idemKey = $"idem-replay-{Guid.NewGuid():N}";

        // First attempt -> Created
        using var firstRes = await SendCheckout(customerClient, payload, idemKey);
        Assert.Equal(HttpStatusCode.Created, firstRes.StatusCode);
        var firstBody = await firstRes.Content.ReadFromJsonAsync<CheckoutResultDto>();

        // Second attempt with exact same key and code -> 200 OK Replay
        using var replayRes = await SendCheckout(customerClient, payload, idemKey);
        Assert.Equal(HttpStatusCode.OK, replayRes.StatusCode);
        Assert.Equal("true", replayRes.Headers.GetValues("Idempotency-Replayed").Single());
        var replayBody = await replayRes.Content.ReadFromJsonAsync<CheckoutResultDto>();
        Assert.Equal(firstBody!.OrderId, replayBody!.OrderId);

        // Verify DB: counters not double allocated
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
        Assert.Equal(1, checkedVoucher.UsageHeld);
        Assert.Equal(30000m, checkedVoucher.BudgetHeld);

        var redemptions = await db.VoucherRedemptions.Where(r => r.VoucherId == vch.Id).ToListAsync();
        Assert.Single(redemptions);
    }

    [Fact]
    public async Task Checkout_Idempotency_SameKeyDifferentCode_RejectsWithKeyReused()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, _, addressId) = await CreateCustomerWithAddressAsync();

        var codeA = $"KEYA_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var codeB = $"KEYB_{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        foreach (var c in new[] { codeA, codeB })
        {
            var res = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
            {
                Code = c,
                DiscountType = "FIXED_AMOUNT",
                DiscountValue = 20000m,
                MinimumOrderAmount = 100000m,
                TotalUsageLimit = 10,
                PerCustomerLimit = 2,
                BudgetTotal = 1000000m,
                StartsAt = DateTime.UtcNow.AddMinutes(-5),
                EndsAt = DateTime.UtcNow.AddDays(7)
            });
            var v = await res.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
            await Send(adminClient, $"admin/vouchers/{v!.Id}/activate");
        }

        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        var idemKey = $"idem-conflict-{Guid.NewGuid():N}";

        // First attempt with codeA -> Created
        var payloadA = new
        {
            voucherCode = codeA,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var resA = await SendCheckout(customerClient, payloadA, idemKey);
        Assert.Equal(HttpStatusCode.Created, resA.StatusCode);

        // Second attempt with SAME key but codeB -> 409 IDEMPOTENCY_KEY_REUSED
        var payloadB = new
        {
            voucherCode = codeB,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var resB = await SendCheckout(customerClient, payloadB, idemKey);
        Assert.Equal(HttpStatusCode.Conflict, resB.StatusCode);
        var problem = await resB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Checkout_Concurrency_LastGlobalSlot_ExactlyOneSucceeds_OneRejects()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (client1, _, addr1) = await CreateCustomerWithAddressAsync();
        var (client2, _, addr2) = await CreateCustomerWithAddressAsync();

        // Replenish stock for SKU 9 so stock is never a bottleneck
        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        await using (var conn = await fixture.OpenDatabaseAsync())
        {
            await using var topUp = new NpgsqlCommand(
                "UPDATE store.inventory_balances SET available_qty = 20, physical_qty = 20, reserved_qty = 0, allocated_qty = 0 WHERE product_sku_id = @sku", conn);
            topUp.Parameters.AddWithValue("sku", sku9);
            await topUp.ExecuteNonQueryAsync();
        }

        // Voucher with total_usage_limit = 1
        var code = $"SLOT_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var createRes = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 30000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 1, // Only 1 slot available!
            PerCustomerLimit = 1,
            BudgetTotal = 1000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        var payload1 = new
        {
            voucherCode = code,
            shippingAddressId = addr1, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        var payload2 = new
        {
            voucherCode = code,
            shippingAddressId = addr2, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };

        // Race concurrently
        var task1 = SendCheckout(client1, payload1, $"race-slot-1-{Guid.NewGuid():N}");
        var task2 = SendCheckout(client2, payload2, $"race-slot-2-{Guid.NewGuid():N}");
        var responses = await Task.WhenAll(task1, task2);

        // Exactly 1 succeeded (201 Created), 1 rejected (409 Conflict)
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var rejected = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VOUCHER_USAGE_LIMIT_REACHED", problem.GetProperty("code").GetString());

        // Verify DB: usage_held is exactly 1
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
        Assert.Equal(1, checkedVoucher.UsageHeld);
        Assert.Equal(1, await db.VoucherRedemptions.CountAsync(r => r.VoucherId == vch.Id));
    }

    [Fact]
    public async Task Checkout_Concurrency_LastBudget_DoesNotOverspend()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (client1, _, addr1) = await CreateCustomerWithAddressAsync();
        var (client2, _, addr2) = await CreateCustomerWithAddressAsync();

        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        await using (var conn = await fixture.OpenDatabaseAsync())
        {
            await using var topUp = new NpgsqlCommand(
                "UPDATE store.inventory_balances SET available_qty = 20, physical_qty = 20, reserved_qty = 0, allocated_qty = 0 WHERE product_sku_id = @sku", conn);
            topUp.Parameters.AddWithValue("sku", sku9);
            await topUp.ExecuteNonQueryAsync();
        }

        // Voucher with budget = 50,000 VND and discount = 50,000 VND -> only 1 customer can use it!
        var code = $"BDG_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var createRes = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 5,
            PerCustomerLimit = 1,
            BudgetTotal = 50000m, // Exactly 1 redemption allowed by budget
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        var payload1 = new
        {
            voucherCode = code,
            shippingAddressId = addr1, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        var payload2 = new
        {
            voucherCode = code,
            shippingAddressId = addr2, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };

        var task1 = SendCheckout(client1, payload1, $"race-bdg-1-{Guid.NewGuid():N}");
        var task2 = SendCheckout(client2, payload2, $"race-bdg-2-{Guid.NewGuid():N}");
        var responses = await Task.WhenAll(task1, task2);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var rejected = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VOUCHER_BUDGET_EXHAUSTED", problem.GetProperty("code").GetString());

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
        Assert.Equal(50000m, checkedVoucher.BudgetHeld);
        Assert.True(checkedVoucher.BudgetHeld <= checkedVoucher.BudgetTotal);
    }

    [Fact]
    public async Task Checkout_Rejection_Rollback_DoesNotLeakCounterOrRedemption()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, _, _) = await CreateCustomerWithAddressAsync();

        var code = $"RLBK_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var createRes = await Send(adminClient, "admin/vouchers", new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 40000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 1,
            BudgetTotal = 1000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        });
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        var sku9 = Guid.Parse("30000000-0000-0000-0000-000000000009");
        var badAddressId = Guid.NewGuid(); // non-existent shipping address

        var payload = new
        {
            voucherCode = code,
            shippingAddressId = badAddressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = sku9, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };

        using var res = await SendCheckout(customerClient, payload, $"idem-rollback-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // Verify that voucher counters did NOT leak and no redemption was created
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
        Assert.Equal(0, checkedVoucher.UsageHeld);
        Assert.Equal(0m, checkedVoucher.BudgetHeld);
        Assert.Equal(0, await db.VoucherRedemptions.CountAsync(r => r.VoucherId == vch.Id));
    }

    private async Task<Guid> CreateSku(Guid actorId, int available = 100, decimal price = 100000m)
    {
        var product = Guid.NewGuid();
        var sku = Guid.NewGuid();
        await using var conn = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at) " +
            "VALUES(@product,@title,'PUBLISHED',@actor,@actor,now()); " +
            "INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) " +
            "VALUES(@sku,@product,@code,'Default',@price,'ACTIVE'); " +
            "INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision) " +
            "VALUES(gen_random_uuid(),@sku,@available,@available,0,0,0);", conn);
        command.Parameters.AddWithValue("product", product);
        command.Parameters.AddWithValue("sku", sku);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("title", "Test Product " + product.ToString("N")[..8]);
        command.Parameters.AddWithValue("code", "SKU-" + sku.ToString("N")[..10].ToUpperInvariant());
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        return sku;
    }

    [Fact]
    public async Task OrderLifecycle_CustomerCancel_ReleasesHeldVoucher_DecrementsHeldCounters_AndAllowsReuse()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, customerId, addressId) = await CreateCustomerWithAddressAsync();
        var skuId = await CreateSku(customerId, 10, 200000m);

        var code = $"CAN_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 1,
            PerCustomerLimit = 1,
            BudgetTotal = 500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        // 1. Checkout with voucher
        var payload1 = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes1 = await SendCheckout(customerClient, payload1, $"cancel-test-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes1.StatusCode);
        var chkDto1 = await chkRes1.Content.ReadFromJsonAsync<CheckoutResultDto>();

        // Verify voucher is HELD
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
            Assert.Equal(1, checkedVoucher.UsageHeld);
            Assert.Equal(50000m, checkedVoucher.BudgetHeld);
            var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == chkDto1!.OrderId);
            Assert.Equal("HELD", redemption.RedemptionStatus);
        }

        // 2. Customer cancels order
        using var cancelRes = await Send(customerClient, $"orders/{chkDto1!.OrderId}/cancel", new
        {
            reason = "Customer changed their mind"
        });
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        // Verify voucher is RELEASED and counters are decremented
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
            Assert.Equal(0, checkedVoucher.UsageHeld);
            Assert.Equal(0, checkedVoucher.UsageConsumed);
            Assert.Equal(0m, checkedVoucher.BudgetHeld);
            Assert.Equal(0m, checkedVoucher.BudgetConsumed);

            var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == chkDto1.OrderId);
            Assert.Equal("RELEASED", redemption.RedemptionStatus);
            Assert.NotNull(redemption.ReleasedAt);

            var audit = await db.AuditLogs.FirstAsync(a => a.EntityId == chkDto1.OrderId && a.Action == "CUSTOMER_ORDER_CANCELLED");
            Assert.Contains(code, audit.BeforeData);
            Assert.Contains(code, audit.AfterData);
        }

        // 3. Customer reuses the same voucher (allowed because limit was freed!)
        var payload2 = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes2 = await SendCheckout(customerClient, payload2, $"cancel-reuse-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes2.StatusCode);
    }

    [Fact]
    public async Task OrderLifecycle_CodExpiry_ReleasesHeldVoucher_DecrementsHeldCounters_AndAllowsReuse()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (customerClient, customerId, addressId) = await CreateCustomerWithAddressAsync();
        var skuId = await CreateSku(customerId, 10, 200000m);

        var code = $"EXP_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 40000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 1,
            PerCustomerLimit = 1,
            BudgetTotal = 500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        // 1. Checkout with voucher
        var payload1 = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes1 = await SendCheckout(customerClient, payload1, $"exp-test-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes1.StatusCode);
        var chkDto1 = await chkRes1.Content.ReadFromJsonAsync<CheckoutResultDto>();

        // 2. Set deadline in the past and trigger expiry
        await using (var conn = await fixture.OpenDatabaseAsync())
        {
            await using var cmd = new NpgsqlCommand("UPDATE store.sales_orders SET created_at = now() - interval '2 hours', deadline_at = now() - interval '1 hour' WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", chkDto1!.OrderId);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var codService = scope.ServiceProvider.GetRequiredService<ICodOrderService>();
            await codService.ExpireDueAsync(100);
        }

        var expireDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < expireDeadline)
        {
            await using var pollDb = await fixture.OpenDatabaseAsync();
            await using var poll = new NpgsqlCommand("SELECT order_status FROM store.sales_orders WHERE id=@order", pollDb);
            poll.Parameters.AddWithValue("order", chkDto1!.OrderId);
            if ((string?)await poll.ExecuteScalarAsync() == "EXPIRED") break;
            await Task.Delay(50);
        }

        // Verify voucher is RELEASED and counters are decremented
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
            Assert.Equal(0, checkedVoucher.UsageHeld);
            Assert.Equal(0, checkedVoucher.UsageConsumed);
            Assert.Equal(0m, checkedVoucher.BudgetHeld);
            Assert.Equal(0m, checkedVoucher.BudgetConsumed);

            var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == chkDto1.OrderId);
            Assert.Equal("RELEASED", redemption.RedemptionStatus);
            Assert.NotNull(redemption.ReleasedAt);

            var order = await db.SalesOrders.FirstAsync(o => o.Id == chkDto1.OrderId);
            Assert.Equal("EXPIRED", order.OrderStatus);

            var audit = await db.AuditLogs.FirstAsync(a => a.EntityId == chkDto1.OrderId && a.Action == "COD_ORDER_EXPIRED");
            Assert.Contains(code, audit.BeforeData);
            Assert.Contains(code, audit.AfterData);
        }

        // 3. Customer reuses the same voucher
        var payload2 = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes2 = await SendCheckout(customerClient, payload2, $"exp-reuse-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes2.StatusCode);
    }

    [Fact]
    public async Task OrderLifecycle_CodConfirm_KeepsHeld_And_DeliveryCompleted_ConsumesExactlyOnce()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (staffClient, staffId, _) = await CreateUserWithRoleAsync("STAFF");
        var (customerClient, customerId, addressId) = await CreateCustomerWithAddressAsync();
        var skuId = await CreateSku(customerId, 10, 200000m);

        var code = $"FUL_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 5,
            PerCustomerLimit = 2,
            BudgetTotal = 500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        // 1. Checkout with voucher
        var payload = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes = await SendCheckout(customerClient, payload, $"ful-test-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes.StatusCode);
        var chkDto = await chkRes.Content.ReadFromJsonAsync<CheckoutResultDto>();
        Assert.Equal(150000m, chkDto!.TotalAmount);

        // 2. Staff confirms COD
        using var confirmRes = await Send(staffClient, $"staff/orders/{chkDto.OrderId}/confirm-cod");
        Assert.Equal(HttpStatusCode.OK, confirmRes.StatusCode);

        // Verify voucher is STILL HELD (not consumed yet!)
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
            Assert.Equal(1, checkedVoucher.UsageHeld);
            Assert.Equal(0, checkedVoucher.UsageConsumed);
            Assert.Equal(50000m, checkedVoucher.BudgetHeld);
            Assert.Equal(0m, checkedVoucher.BudgetConsumed);

            var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == chkDto.OrderId);
            Assert.Equal("HELD", redemption.RedemptionStatus);
        }

        // 3. Staff starts fulfillment, packs, and hands over
        using var startRes = await Send(staffClient, $"staff/orders/{chkDto.OrderId}/start-fulfillment");
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);
        var startDto = await startRes.Content.ReadFromJsonAsync<FulfillmentOrderDto>();
        var shipmentId = startDto!.ShipmentId!.Value;

        using var packRes = await Send(staffClient, $"staff/shipments/{shipmentId}/pack");
        Assert.Equal(HttpStatusCode.OK, packRes.StatusCode);

        using var handoverRes = await Send(staffClient, $"staff/shipments/{shipmentId}/handover");
        Assert.Equal(HttpStatusCode.OK, handoverRes.StatusCode);

        // 4. Staff records successful delivery + COD collected
        var deliveryPayload = new
        {
            result = "DELIVERED",
            codCollectedAmount = 150000m,
            collectionReference = $"REF-{Guid.NewGuid():N}"[..16]
        };
        using var deliveryRes = await Send(staffClient, $"staff/shipments/{shipmentId}/delivery-result", deliveryPayload);
        Assert.Equal(HttpStatusCode.OK, deliveryRes.StatusCode);

        // Verify voucher is now CONSUMED exactly once!
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
            Assert.Equal(0, checkedVoucher.UsageHeld);
            Assert.Equal(1, checkedVoucher.UsageConsumed);
            Assert.Equal(0m, checkedVoucher.BudgetHeld);
            Assert.Equal(50000m, checkedVoucher.BudgetConsumed);

            var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == chkDto.OrderId);
            Assert.Equal("CONSUMED", redemption.RedemptionStatus);
            Assert.NotNull(redemption.ConsumedAt);

            var audit = await db.AuditLogs.FirstAsync(a => a.EntityId == chkDto.OrderId && a.Action == "COD_DELIVERY_COMPLETED");
            Assert.Contains(code, audit.BeforeData);
            Assert.Contains(code, audit.AfterData);

            // Verify idempotency of ConsumeForCompletedOrderAsync
            var lifecycle = scope.ServiceProvider.GetRequiredService<IVoucherLifecycleManager>();
            await lifecycle.ConsumeForCompletedOrderAsync(chkDto.OrderId, DateTime.UtcNow);
            // Counters must remain unchanged
            Assert.Equal(0, checkedVoucher.UsageHeld);
            Assert.Equal(1, checkedVoucher.UsageConsumed);
        }
    }

    [Fact]
    public async Task OrderLifecycle_DeliveryFailed_ReturnStock_ReleasesHeldVoucher_AndAllowsReuse()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (staffClient, staffId, _) = await CreateUserWithRoleAsync("STAFF");
        var (customerClient, customerId, addressId) = await CreateCustomerWithAddressAsync();
        var skuId = await CreateSku(customerId, 10, 200000m);

        var code = $"RET_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 1,
            PerCustomerLimit = 1,
            BudgetTotal = 500000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        // 1. Checkout with voucher
        var payload = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes = await SendCheckout(customerClient, payload, $"ret-test-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes.StatusCode);
        var chkDto = await chkRes.Content.ReadFromJsonAsync<CheckoutResultDto>();

        // 2. Staff confirms COD, starts fulfillment, packs, hands over
        using var confirmRes = await Send(staffClient, $"staff/orders/{chkDto!.OrderId}/confirm-cod");
        Assert.Equal(HttpStatusCode.OK, confirmRes.StatusCode);

        using var startRes = await Send(staffClient, $"staff/orders/{chkDto.OrderId}/start-fulfillment");
        var startDto = await startRes.Content.ReadFromJsonAsync<FulfillmentOrderDto>();
        var shipmentId = startDto!.ShipmentId!.Value;

        using var packRes = await Send(staffClient, $"staff/shipments/{shipmentId}/pack");
        using var handoverRes = await Send(staffClient, $"staff/shipments/{shipmentId}/handover");

        // 3. Delivery fails
        var failPayload = new
        {
            result = "FAILED",
            failureReason = "Khach hang khong nghe may"
        };
        using var failRes = await Send(staffClient, $"staff/shipments/{shipmentId}/delivery-result", failPayload);
        Assert.Equal(HttpStatusCode.OK, failRes.StatusCode);

        // 4. Staff completes return stock
        var returnPayload = new
        {
            receivedEvidence = "Hang da duoc tra ve kho nguyen ven"
        };
        using var returnRes = await Send(staffClient, $"staff/shipments/{shipmentId}/return-stock", returnPayload);
        Assert.Equal(HttpStatusCode.OK, returnRes.StatusCode);

        // Verify voucher is RELEASED and counters are decremented
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var checkedVoucher = await db.Vouchers.FirstAsync(v => v.Id == vch.Id);
            Assert.Equal(0, checkedVoucher.UsageHeld);
            Assert.Equal(0, checkedVoucher.UsageConsumed);
            Assert.Equal(0m, checkedVoucher.BudgetHeld);
            Assert.Equal(0m, checkedVoucher.BudgetConsumed);

            var redemption = await db.VoucherRedemptions.FirstAsync(r => r.OrderId == chkDto.OrderId);
            Assert.Equal("RELEASED", redemption.RedemptionStatus);
            Assert.NotNull(redemption.ReleasedAt);

            var order = await db.SalesOrders.FirstAsync(o => o.Id == chkDto.OrderId);
            Assert.Equal("CANCELLED", order.OrderStatus);
            Assert.Equal("DELIVERY_FAILED_RETURNED", order.ClosedReason);

            var audit = await db.AuditLogs.FirstAsync(a => a.EntityId == chkDto.OrderId && a.Action == "DELIVERY_RETURN_STOCK_COMPLETED");
            Assert.Contains(code, audit.BeforeData);
            Assert.Contains(code, audit.AfterData);
        }

        // 5. Customer reuses the same voucher
        var payload2 = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes2 = await SendCheckout(customerClient, payload2, $"ret-reuse-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes2.StatusCode);
    }

    [Fact]
    public async Task OrderSnapshots_And_Reporting_VerifyVoucherFields_AndRevenueAmount()
    {
        var (adminClient, _, _) = await CreateUserWithRoleAsync("ADMIN");
        var (staffClient, _, _) = await CreateUserWithRoleAsync("STAFF");
        var (customerClient, customerId, addressId) = await CreateCustomerWithAddressAsync();
        var skuId = await CreateSku(customerId, 10, 200000m);

        var code = $"SNP_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var vchReq = new CreateVoucherRequest
        {
            Code = code,
            DiscountType = "FIXED_AMOUNT",
            DiscountValue = 50000m,
            MinimumOrderAmount = 100000m,
            TotalUsageLimit = 10,
            PerCustomerLimit = 2,
            BudgetTotal = 1000000m,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            EndsAt = DateTime.UtcNow.AddDays(7)
        };
        var createRes = await Send(adminClient, "admin/vouchers", vchReq);
        var vch = await createRes.Content.ReadFromJsonAsync<AdminVoucherDetailDto>();
        await Send(adminClient, $"admin/vouchers/{vch!.Id}/activate");

        // 1. Checkout
        var payload = new
        {
            voucherCode = code,
            shippingAddressId = addressId, shippingMethodId = ShippingCheckoutTestDefaults.MethodId, shippingMethodRevision = ShippingCheckoutTestDefaults.Revision, quotedShippingFee = ShippingCheckoutTestDefaults.Fee,
            paymentMethod = "COD",
            lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
        };
        using var chkRes = await SendCheckout(customerClient, payload, $"snp-test-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, chkRes.StatusCode);
        var chkDto = await chkRes.Content.ReadFromJsonAsync<CheckoutResultDto>();

        // 2. Customer order query
        using var orderRes = await customerClient.GetAsync($"/api/v1/orders/{chkDto!.OrderId}");
        Assert.Equal(HttpStatusCode.OK, orderRes.StatusCode);
        var orderDto = await orderRes.Content.ReadFromJsonAsync<CustomerOrderDto>();
        Assert.NotNull(orderDto);
        Assert.Equal(200000m, orderDto.SubtotalAmount);
        Assert.Equal(50000m, orderDto.VoucherDiscountAmount);
        Assert.Equal(code, orderDto.VoucherCode);
        Assert.Equal(150000m, orderDto.TotalAmount);

        // 3. Staff COD pending query
        using var codPendingRes = await staffClient.GetAsync("/api/v1/staff/orders/cod-pending");
        Assert.Equal(HttpStatusCode.OK, codPendingRes.StatusCode);
        var codOrders = await codPendingRes.Content.ReadFromJsonAsync<List<CodOrderDto>>();
        var codOrder = codOrders?.FirstOrDefault(o => o.OrderId == chkDto.OrderId);
        Assert.NotNull(codOrder);
        Assert.Equal(200000m, codOrder.SubtotalAmount);
        Assert.Equal(50000m, codOrder.VoucherDiscountAmount);
        Assert.Equal(code, codOrder.VoucherCode);
        Assert.Equal(150000m, codOrder.TotalAmount);

        // 4. Complete delivery and verify fulfillment DTO snapshots
        using var confirmRes = await Send(staffClient, $"staff/orders/{chkDto.OrderId}/confirm-cod");
        var confirmDto = await confirmRes.Content.ReadFromJsonAsync<CodOrderDto>();
        Assert.Equal(200000m, confirmDto!.SubtotalAmount);
        Assert.Equal(50000m, confirmDto.VoucherDiscountAmount);
        Assert.Equal(code, confirmDto.VoucherCode);

        using var startRes = await Send(staffClient, $"staff/orders/{chkDto.OrderId}/start-fulfillment");
        var startDto = await startRes.Content.ReadFromJsonAsync<FulfillmentOrderDto>();
        Assert.Equal(200000m, startDto!.SubtotalAmount);
        Assert.Equal(50000m, startDto.VoucherDiscountAmount);
        Assert.Equal(code, startDto.VoucherCode);
        var shipmentId = startDto.ShipmentId!.Value;

        using var packRes = await Send(staffClient, $"staff/shipments/{shipmentId}/pack");
        using var handoverRes = await Send(staffClient, $"staff/shipments/{shipmentId}/handover");
        var deliveryPayload = new
        {
            result = "DELIVERED",
            codCollectedAmount = 150000m,
            collectionReference = $"REF-{Guid.NewGuid():N}"[..16]
        };
        using var deliveryRes = await Send(staffClient, $"staff/shipments/{shipmentId}/delivery-result", deliveryPayload);
        Assert.Equal(HttpStatusCode.OK, deliveryRes.StatusCode);
        var deliveryDto = await deliveryRes.Content.ReadFromJsonAsync<FulfillmentOrderDto>();
        Assert.Equal(200000m, deliveryDto!.SubtotalAmount);
        Assert.Equal(50000m, deliveryDto.VoucherDiscountAmount);
        Assert.Equal(code, deliveryDto.VoucherCode);
        Assert.Equal(150000m, deliveryDto.TotalAmount);
    }
}
