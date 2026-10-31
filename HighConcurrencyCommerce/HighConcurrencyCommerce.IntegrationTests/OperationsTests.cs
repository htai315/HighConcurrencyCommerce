using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class OperationsTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task Liveness_and_readiness_are_anonymous_machine_readable_and_no_store()
    {
        using var client = fixture.CreateClient();
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.True(live.Headers.CacheControl?.NoStore);
        var liveBody = await live.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", liveBody.GetProperty("status").GetString());
        Assert.Equal("self", Assert.Single(liveBody.GetProperty("checks").EnumerateArray()).GetProperty("name").GetString());

        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(ready.Headers.CacheControl?.NoStore);
        var readyBody = await ready.Content.ReadFromJsonAsync<JsonElement>();
        var check = Assert.Single(readyBody.GetProperty("checks").EnumerateArray());
        Assert.Equal("postgresql", check.GetProperty("name").GetString());
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        Assert.DoesNotContain("connection", readyBody.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Correlation_id_is_propagated_to_problem_details_and_invalid_input_is_replaced()
    {
        using var client = fixture.CreateClient();
        const string supplied = "client-order-123";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders");
        request.Headers.Add("X-Correlation-ID", supplied);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(supplied, response.Headers.GetValues("X-Correlation-ID").Single());
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(supplied, problem.GetProperty("correlationId").GetString());

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        invalidRequest.Headers.Add("X-Correlation-ID", new string('x', 65));
        using var replaced = await client.SendAsync(invalidRequest);
        var generated = replaced.Headers.GetValues("X-Correlation-ID").Single();
        Assert.Matches("^[0-9a-f]{32}$", generated);
    }
}
