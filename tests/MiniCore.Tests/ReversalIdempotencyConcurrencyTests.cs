using System.Net;
using System.Text.Json;

namespace MiniCore.Tests;

[Collection("db")]
public class ReversalIdempotencyConcurrencyTests : IAsyncLifetime
{
    private readonly ApiFixture _api;

    public ReversalIdempotencyConcurrencyTests(ApiFixture api) => _api = api;

    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> CreateTransferAsync(string user, string source, string destination, decimal amount)
    {
        var response = await _api.TransferAsync(user, source, destination, amount);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiFixture.JsonOf(response)).GetProperty("transferId").GetGuid();
    }

    private Task<HttpResponseMessage> ReverseAsync(string user, Guid transferId, string? key = "auto") =>
        _api.SendAsync(user, HttpMethod.Post, $"/api/transfers/{transferId}/reverse",
            idempotencyKey: key == "auto" ? Guid.NewGuid().ToString() : key);


    [Fact]
    public async Task E8_operator_reverses_once_and_returns_amount_plus_fee()
    {
        var id = await CreateTransferAsync("CUST-01", "ACC-1001", "ACC-1002", 1000m);

        var first = await ReverseAsync("OPERATOR", id);
        var second = await ReverseAsync("OPERATOR", id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(10000m, await _api.BalanceAsync("ACC-1001")); 
        Assert.Equal(250m, await _api.BalanceAsync("ACC-1002"));  
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("TRANSFER_ALREADY_REVERSED", await ApiFixture.CodeOf(second));
    }

    [Fact]
    public async Task E8_only_operators_can_reverse()
    {
        var id = await CreateTransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m);

        Assert.Equal(HttpStatusCode.Forbidden, (await ReverseAsync("CUST-01", id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReverseAsync("AUDITOR", id)).StatusCode);
    }

    [Fact]
    public async Task E8_reversal_expires_after_24_hours()
    {
        var id = await CreateTransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m);

        _api.Clock.Advance(TimeSpan.FromHours(25)); 
        var response = await ReverseAsync("OPERATOR", id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("REVERSAL_WINDOW_EXPIRED", await ApiFixture.CodeOf(response));
    }

    [Fact]
    public async Task E8_reversal_is_rejected_when_destination_cannot_return_the_money()
    {
        var id = await CreateTransferAsync("CUST-01", "ACC-1001", "ACC-1002", 1000m); 
        await CreateTransferAsync("CUST-02", "ACC-1002", "ACC-1001", 1200m);          

        var response = await ReverseAsync("OPERATOR", id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("REVERSAL_INSUFFICIENT_FUNDS", await ApiFixture.CodeOf(response));
    }

    [Fact]
    public async Task E8_reversal_frees_the_daily_limit()
    {
        var id = await CreateTransferAsync("CUST-04", "ACC-1004", "ACC-1001", 5000m); 
        var blocked = await _api.TransferAsync("CUST-04", "ACC-1004", "ACC-1001", 1m);
        await ReverseAsync("OPERATOR", id);
        var afterReversal = await _api.TransferAsync("CUST-04", "ACC-1004", "ACC-1001", 1m);

        Assert.Equal("DAILY_LIMIT_EXCEEDED", await ApiFixture.CodeOf(blocked));
        Assert.Equal(HttpStatusCode.Created, afterReversal.StatusCode);
    }


    [Fact]
    public async Task E9_same_key_same_request_replays_the_original_response_without_repeating_the_effect()
    {
        var key = Guid.NewGuid().ToString();

        var first = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key);
        var retry = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.TryGetValues("Idempotent-Replayed", out var replayed));
        Assert.Equal("true", replayed!.Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync()); 
        Assert.Equal(9900m, await _api.BalanceAsync("ACC-1001"));  
        Assert.Equal(2, await _api.MovementCountAsync());          
    }

    [Fact]
    public async Task E9_same_key_with_a_different_payload_is_rejected()
    {
        var key = Guid.NewGuid().ToString();
        await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key);

        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 200m, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiFixture.CodeOf(response));
    }

    [Fact]
    public async Task E9_missing_key_is_a_400()
    {
        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MISSING_IDEMPOTENCY_KEY", await ApiFixture.CodeOf(response));
    }

    [Fact]
    public async Task E9_key_longer_than_100_chars_is_invalid_request_not_missing_key()
    {
        var longKey = new string('k', 101);
        var tooLong = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key: longKey);
        var exactly100 = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key: new string('k', 100));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ApiFixture.CodeOf(tooLong));
        Assert.Equal(HttpStatusCode.Created, exactly100.StatusCode); 
    }

    [Fact]
    public async Task E9_simultaneous_requests_with_the_same_key_execute_only_once()
    {
        var key = Guid.NewGuid().ToString();
        await _api.TokenAsync("CUST-01");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = new HashSet<Guid>();
        foreach (var r in responses)
            ids.Add((await ApiFixture.JsonOf(r)).GetProperty("transferId").GetGuid());
        Assert.Single(ids);                                      
        Assert.Equal(9900m, await _api.BalanceAsync("ACC-1001")); 
    }

    [Fact]
    public async Task E9_burst_of_150_retries_with_the_same_key_does_not_exhaust_the_connection_pool()
    {
        const int retries = 150;
        var key = Guid.NewGuid().ToString();
        await _api.TokenAsync("CUST-01");

        var responses = await Task.WhenAll(Enumerable.Range(0, retries)
            .Select(_ => _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode)); 
        Assert.Equal(retries - 1, responses.Count(r => r.Headers.Contains("Idempotent-Replayed")));
        Assert.Equal(9900m, await _api.BalanceAsync("ACC-1001")); 
        Assert.Equal(2, await _api.MovementCountAsync());          
    }


    [Fact]
    public async Task E10_ten_simultaneous_transfers_never_overdraw_the_account()
    {
        await _api.TokenAsync("CUST-02");
        const decimal totalBefore = 10000m + 250m + 5000m + 120000m + 0m + 501m;

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _api.TransferAsync("CUST-02", "ACC-1002", "ACC-1001", 100m)));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToList();
        Assert.Equal(2, created);
        Assert.Equal(8, rejected.Count);
        foreach (var r in rejected)
            Assert.Equal("INSUFFICIENT_FUNDS", await ApiFixture.CodeOf(r));

        Assert.Equal(50m, await _api.BalanceAsync("ACC-1002"));
        var totalAfter = 0m;
        foreach (var id in new[] { "ACC-1001", "ACC-1002", "ACC-1003", "ACC-1004", "ACC-1005", "ACC-1006" })
            totalAfter += await _api.BalanceAsync(id);
        Assert.Equal(totalBefore, totalAfter); 
    }

    [Fact]
    public async Task Crossed_transfers_do_not_deadlock()
    {
        await _api.TokenAsync("CUST-01");
        await _api.TokenAsync("CUST-04");

        var tasks = Enumerable.Range(0, 10).Select(i => i % 2 == 0
            ? _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1004", 10m)
            : _api.TransferAsync("CUST-04", "ACC-1004", "ACC-1001", 10m));
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
    }
}
