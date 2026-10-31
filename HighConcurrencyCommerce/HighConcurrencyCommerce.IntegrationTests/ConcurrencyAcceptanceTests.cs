using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ConcurrencyAcceptanceTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private sealed record Buyer(HttpClient Client, Guid AddressId);

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Checkout(
        HttpClient client, Guid addressId, Guid skuId, string idempotencyKey, string csrfToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkouts")
        {
            Content = JsonContent.Create(new
            {
                shippingAddressId = addressId,
                paymentMethod = "COD",
                lines = new[] { new { productSkuId = skuId, quantity = 1, flashSaleOfferId = (Guid?)null } }
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task AC20a_one_hundred_concurrent_buyers_competing_for_ten_units_never_oversell()
    {
        const int buyerCount = 100;
        await using var db = await fixture.OpenDatabaseAsync();
        await using var roleCommand = new NpgsqlCommand("SELECT id FROM store.roles WHERE role_key='CUSTOMER'", db);
        var customerRoleId = (Guid)(await roleCommand.ExecuteScalarAsync())!;
        var buyerSeeds = Enumerable.Range(0, buyerCount).Select(_ => new
        {
            UserId = Guid.NewGuid(), AddressId = Guid.NewGuid(), Token = CreateToken()
        }).ToArray();
        await using (var tx = await db.BeginTransactionAsync())
        {
            foreach (var (seed, index) in buyerSeeds.Select((value, index) => (value, index)))
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status)
                    VALUES(@user,@role,@email,'acceptance-test-no-login','AC20a Buyer','ACTIVE');
                    INSERT INTO store.customer_addresses(id,customer_id,recipient_name,phone,address_line,province,is_default)
                    VALUES(@address,@user,'Người nhận AC20a','0901234567','Sân cầu concurrency','TP Hồ Chí Minh',true);
                    INSERT INTO store.auth_sessions(token_hash,user_id,created_at,expires_at)
                    VALUES(@hash,@user,now(),now()+interval '1 day');
                    """, db, tx);
                command.Parameters.AddWithValue("user", seed.UserId);
                command.Parameters.AddWithValue("role", customerRoleId);
                command.Parameters.AddWithValue("email", $"ac20a_{index}_{Guid.NewGuid():N}@example.invalid");
                command.Parameters.AddWithValue("address", seed.AddressId);
                command.Parameters.AddWithValue("hash", HashToken(seed.Token));
                await command.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        await using (var seedSku = new NpgsqlCommand("""
            INSERT INTO store.products(id,title,publication_status,created_by,published_by,published_at)
            VALUES(@product,'Vợt AC20a','PUBLISHED',@actor,@actor,now());
            INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status)
            VALUES(@sku,@product,@code,'4U G5',500000,'ACTIVE');
            INSERT INTO store.inventory_balances(id,product_sku_id,physical_qty,available_qty,reserved_qty,allocated_qty,revision)
            VALUES(gen_random_uuid(),@sku,10,10,0,0,0);
            """, db))
        {
            seedSku.Parameters.AddWithValue("product", productId);
            seedSku.Parameters.AddWithValue("sku", skuId);
            seedSku.Parameters.AddWithValue("actor", buyerSeeds[0].UserId);
            seedSku.Parameters.AddWithValue("code", "AC20A-" + skuId.ToString("N")[..12]);
            await seedSku.ExecuteNonQueryAsync();
        }

        using WebApplicationFactory<Program> app = fixture.CreateConcurrentApplication();
        var buyers = buyerSeeds.Select(seed =>
        {
            var client = app.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
            });
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"__Host-hcc-session={seed.Token}");
            return new Buyer(client, seed.AddressId);
        }).ToArray();

        try
        {
            var csrfTokens = await Task.WhenAll(buyers.Select(buyer => Csrf(buyer.Client)));
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkoutTasks = buyers.Select(async (buyer, index) =>
            {
                await startGate.Task;
                return await Checkout(buyer.Client, buyer.AddressId, skuId,
                    $"ac20a-{index:D3}-{Guid.NewGuid():N}", csrfTokens[index]);
            }).ToArray();
            startGate.SetResult();
            var responses = await Task.WhenAll(checkoutTasks);
            Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
            Assert.Equal(90, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            foreach (var rejected in responses.Where(x => x.StatusCode == HttpStatusCode.Conflict))
            {
                var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("OUT_OF_STOCK", body.GetProperty("code").GetString());
            }
            foreach (var response in responses) response.Dispose();
        }
        finally
        {
            foreach (var buyer in buyers) buyer.Client.Dispose();
        }

        await using var verify = new NpgsqlCommand("""
            SELECT physical_qty,available_qty,reserved_qty,allocated_qty FROM store.inventory_balances WHERE product_sku_id=@sku;
            SELECT count(*) FROM store.sales_order_items WHERE product_sku_id=@sku;
            SELECT count(*) FROM store.inventory_movements WHERE product_sku_id=@sku AND movement_type='RESERVE';
            SELECT count(*) FILTER (WHERE request_status='SUCCEEDED'),count(*) FILTER (WHERE request_status='REJECTED')
            FROM store.checkout_requests WHERE idempotency_key LIKE 'ac20a-%';
            """, db);
        verify.Parameters.AddWithValue("sku", skuId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(10, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(10L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(10L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
        Assert.Equal(10L, reader.GetInt64(0)); Assert.Equal(90L, reader.GetInt64(1));
    }
}
