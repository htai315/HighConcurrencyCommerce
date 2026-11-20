using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HighConcurrencyCommerce.Infrastructure.Catalog;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ProductSummaryBatchTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string AlphaId = "20000000-0000-0000-0000-000000000001";
    private const string BetaId = "20000000-0000-0000-0000-000000000002";
    private const string GammaId = "20000000-0000-0000-0000-000000000003";
    private const string DraftId = "20000000-0000-0000-0000-000000000004";

    [Fact]
    public async Task Anonymous_batch_preserves_request_order_and_public_projection()
    {
        using var response = await fixture.Client.GetAsync(Url(BetaId, AlphaId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToArray();

        // Exactly 2 items in the EXACT requested order: Beta then Alpha
        Assert.Equal([BetaId, AlphaId], items.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(2, body.GetProperty("requestedCount").GetInt32());
        Assert.Equal(0, body.GetProperty("omittedCount").GetInt32());

        Assert.DoesNotContain("publicationStatus", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalQty", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_public_and_missing_products_are_omitted_without_lifecycle_disclosure()
    {
        const string missingId = "99999999-0000-0000-0000-000000000000";

        using var response = await fixture.Client.GetAsync(Url(AlphaId, DraftId, missingId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("requestedCount").GetInt32());
        Assert.Equal(2, body.GetProperty("omittedCount").GetInt32());

        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(AlphaId, items[0].GetProperty("id").GetString());

        Assert.DoesNotContain("DRAFT", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?id=invalid")]
    [InlineData("?id=00000000-0000-0000-0000-000000000000")]
    [InlineData("?id=20000000-0000-0000-0000-000000000001&id=20000000-0000-0000-0000-000000000001")]
    public async Task Invalid_batch_query_returns_validation_problem_never_500(string query)
    {
        using var response = await fixture.Client.GetAsync("/api/v1/catalog/products/batch" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Batch_reader_executes_exactly_one_database_command_with_restored_timeout()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        var observer = new CommandCounter();
        var options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(observer)
            .Options;
        await using var db = new StoreDbContext(options);
        var reader = new PostgresCatalogReader(db);

        var result = await reader.GetProductSummariesAsync(
            [Guid.Parse(AlphaId), Guid.Parse(BetaId)],
            CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Single(observer.Commands);
        Assert.All(observer.CommandTimeouts, timeout => Assert.Equal(2, timeout));
        Assert.Null(db.Database.GetCommandTimeout());
    }

    private static string Url(params string[] ids) =>
        "/api/v1/catalog/products/batch?" + string.Join("&", ids.Select(id => "id=" + Uri.EscapeDataString(id)));

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public ConcurrentBag<string> Commands { get; } = [];
        public ConcurrentBag<int> CommandTimeouts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            CommandTimeouts.Add(command.CommandTimeout);
            return ValueTask.FromResult(result);
        }
    }
}

