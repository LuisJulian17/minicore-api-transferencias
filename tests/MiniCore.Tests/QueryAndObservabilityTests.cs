using System.Net;

namespace MiniCore.Tests;

[Collection("db")]
public class QueryAndObservabilityTests : IAsyncLifetime
{
    private readonly ApiFixture _api;

    public QueryAndObservabilityTests(ApiFixture api) => _api = api;

    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task F2_movements_are_paginated_newest_first_and_size_is_capped_at_100()
    {
        for (var i = 0; i < 3; i++)
        {
            _api.Clock.Advance(TimeSpan.FromMinutes(1)); 
            await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m);
        }

        var page1 = await ApiFixture.JsonOf(
            await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1001/movements?page=1&size=2"));
        var page2 = await ApiFixture.JsonOf(
            await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1001/movements?page=2&size=2"));
        var tooBig = await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1001/movements?size=101");

        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(9700m, page1.GetProperty("items")[0].GetProperty("balanceAfter").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ApiFixture.CodeOf(tooBig));
    }

    [Fact]
    public async Task F2_date_filters_use_a_half_open_range()
    {
        await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m); 

        var today = _api.Clock.GetUtcNow().UtcDateTime.Date;
        string Url(DateTime from, DateTime to) =>
            $"/api/accounts/ACC-1001/movements?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        var inRange = await ApiFixture.JsonOf(await _api.SendAsync("CUST-01", HttpMethod.Get, Url(today, today.AddDays(1))));
        var before = await ApiFixture.JsonOf(await _api.SendAsync("CUST-01", HttpMethod.Get, Url(today.AddDays(-5), today)));

        Assert.Equal(1, inRange.GetProperty("items").GetArrayLength());
        Assert.Equal(0, before.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task F3_owner_and_operator_see_the_transfer_but_other_clients_do_not()
    {
        var created = await ApiFixture.JsonOf(await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m));
        var id = created.GetProperty("transferId").GetGuid();

        var owner = await _api.SendAsync("CUST-01", HttpMethod.Get, $"/api/transfers/{id}");
        var receiver = await _api.SendAsync("CUST-02", HttpMethod.Get, $"/api/transfers/{id}");
        var op = await _api.SendAsync("OPERATOR", HttpMethod.Get, $"/api/transfers/{id}");
        var stranger = await _api.SendAsync("CUST-04", HttpMethod.Get, $"/api/transfers/{id}");
        var missing = await _api.SendAsync("CUST-01", HttpMethod.Get, $"/api/transfers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, receiver.StatusCode);
        Assert.Equal(HttpStatusCode.OK, op.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CorrelationId_is_accepted_generated_and_present_in_errors()
    {
        var provided = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1001", 10m,
            tweak: request => request.Headers.Add("X-Correlation-Id", "test-corr-123"));
        var generated = await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1001/balance");
        var hostile = await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1001/balance",
            tweak: request => request.Headers.Add("X-Correlation-Id", "bad value;with spaces"));

        Assert.Equal("test-corr-123", provided.Headers.GetValues("X-Correlation-Id").Single());
        var problem = await ApiFixture.JsonOf(provided);
        Assert.Equal("test-corr-123", problem.GetProperty("correlationId").GetString()); 
        Assert.Equal("SAME_ACCOUNT", problem.GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(generated.Headers.GetValues("X-Correlation-Id").Single()));
        Assert.NotEqual("bad value;with spaces", hostile.Headers.GetValues("X-Correlation-Id").Single()); 
    }

    [Fact]
    public async Task Error_responses_are_problem_details_without_internal_details()
    {
        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 0m);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", text, StringComparison.OrdinalIgnoreCase);
    }
}
