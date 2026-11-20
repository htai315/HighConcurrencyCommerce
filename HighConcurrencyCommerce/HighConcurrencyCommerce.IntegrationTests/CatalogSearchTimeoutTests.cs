using System.Collections.Concurrent;
using System.Data.Common;
using HighConcurrencyCommerce.Application.Catalog;
using HighConcurrencyCommerce.Infrastructure.Catalog;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CatalogSearchTimeoutTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private sealed class CommandTimeoutObserver : DbCommandInterceptor
    {
        public ConcurrentBag<int> ObservedTimeouts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ObservedTimeouts.Add(command.CommandTimeout);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            ObservedTimeouts.Add(command.CommandTimeout);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Public_search_and_suggestions_use_two_second_command_timeout_and_restore_context_setting()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        var observer = new CommandTimeoutObserver();
        var options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(observer)
            .Options;
        await using var db = new StoreDbContext(options);
        var catalog = new PostgresCatalogReader(db);

        Assert.Null(db.Database.GetCommandTimeout());
        await catalog.GetProductsAsync(
            new NormalizedCatalogQuery("vot", null, null, null, null, null, null, 1, 12, CatalogSort.Relevance),
            CancellationToken.None);
        await catalog.GetSuggestionsAsync("vot", 8, CancellationToken.None);

        Assert.NotEmpty(observer.ObservedTimeouts);
        Assert.All(observer.ObservedTimeouts, timeout => Assert.Equal(2, timeout));
        Assert.Null(db.Database.GetCommandTimeout());
    }
}
