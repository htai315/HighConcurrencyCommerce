using System.Net;
using HighConcurrencyCommerce.Application.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class CatalogSuggestionRateLimitTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task Suggestions_have_an_independent_fixed_window_rate_limit()
    {
        await using var application = fixture.CreateRateLimitedApplication();
        using var client = application.CreateClient();

        for (var index = 0; index < 60; index++)
        {
            using var allowed = await client.GetAsync("/api/v1/catalog/search/suggestions?q=a");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await client.GetAsync("/api/v1/catalog/search/suggestions?q=a");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Suggestion_reader_honors_a_pre_cancelled_token()
    {
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICatalogReader>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.GetSuggestionsAsync("alpha", 8, cancellation.Token));
    }
}
