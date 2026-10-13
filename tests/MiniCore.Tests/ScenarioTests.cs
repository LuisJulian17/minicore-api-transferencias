using System.Net;

namespace MiniCore.Tests;

[Collection("db")]
public class ScenarioTests : IAsyncLifetime
{
    private readonly ApiFixture _api;

    public ScenarioTests(ApiFixture api) => _api = api;

    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task E1_transfer_up_to_500_has_no_fee()
    {
        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 100m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFixture.JsonOf(response);
        Assert.Equal(0m, body.GetProperty("fee").GetDecimal());
        Assert.Equal(9900m, await _api.BalanceAsync("ACC-1001"));
        Assert.Equal(350m, await _api.BalanceAsync("ACC-1002"));
    }

    [Fact]
    public async Task E2_transfer_of_1000_charges_a_fee_of_5()
    {
        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 1000m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFixture.JsonOf(response);
        Assert.Equal(5.00m, body.GetProperty("fee").GetDecimal());
        Assert.Equal(1005.00m, body.GetProperty("totalDebited").GetDecimal());
        Assert.Equal(8995.00m, body.GetProperty("sourceBalanceAfter").GetDecimal());
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal(8995.00m, await _api.BalanceAsync("ACC-1001"));
        Assert.Equal(1250.00m, await _api.BalanceAsync("ACC-1002"));
    }

    [Theory]
    [InlineData(501.00, 3.00)]   
    [InlineData(1234.57, 6.17)]  
    public async Task E3_fee_minimum_and_rounding(double amount, double expectedFee)
    {
        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", (decimal)amount);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiFixture.JsonOf(response);
        Assert.Equal((decimal)expectedFee, body.GetProperty("fee").GetDecimal());
    }

    [Fact]
    public async Task E4_balance_must_cover_amount_plus_fee()
    {
        var response = await _api.TransferAsync("CUST-06", "ACC-1006", "ACC-1002", 501m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("INSUFFICIENT_FUNDS", await ApiFixture.CodeOf(response));
        Assert.Equal(501m, await _api.BalanceAsync("ACC-1006")); 
    }

    [Fact]
    public async Task E5_daily_limit_is_inclusive()
    {
        var first = await _api.TransferAsync("CUST-04", "ACC-1004", "ACC-1001", 3000m);
        var second = await _api.TransferAsync("CUST-04", "ACC-1004", "ACC-1001", 2000m);
        var third = await _api.TransferAsync("CUST-04", "ACC-1004", "ACC-1001", 1m);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("DAILY_LIMIT_EXCEEDED", await ApiFixture.CodeOf(third));
    }

    [Fact]
    public async Task E6_blocked_source_and_closed_destination_are_rejected()
    {
        var fromBlocked = await _api.TransferAsync("CUST-03", "ACC-1003", "ACC-1002", 100m);
        var toClosed = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1005", 100m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, fromBlocked.StatusCode);
        Assert.Equal("ACCOUNT_NOT_ACTIVE", await ApiFixture.CodeOf(fromBlocked));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toClosed.StatusCode);
        Assert.Equal("ACCOUNT_NOT_ACTIVE", await ApiFixture.CodeOf(toClosed));
    }

    [Theory]
    [InlineData("ACC-1001", "ACC-1002", 0, 422, "INVALID_AMOUNT")]
    [InlineData("ACC-1001", "ACC-1002", -5, 422, "INVALID_AMOUNT")]
    [InlineData("ACC-1001", "ACC-1002", 10.001, 422, "INVALID_AMOUNT")]
    [InlineData("ACC-1001", "ACC-1001", 10, 422, "SAME_ACCOUNT")]
    [InlineData("ACC-1001", "ACC-9999", 10, 404, "ACCOUNT_NOT_FOUND")]
    public async Task E7_invalid_requests_change_nothing(string source, string destination, double amount,
        int expectedStatus, string expectedCode)
    {
        var response = await _api.TransferAsync("CUST-01", source, destination, (decimal)amount);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(expectedCode, await ApiFixture.CodeOf(response));
        Assert.Equal(10000m, await _api.BalanceAsync("ACC-1001"));
        Assert.Equal(250m, await _api.BalanceAsync("ACC-1002"));
        Assert.Equal(0, await _api.MovementCountAsync());
    }

    [Theory]
    [InlineData("ACC-9999", "ACC-1002", 404)] 
    [InlineData("ACC-1002", "ACC-9999", 403)] 
    [InlineData("ACC-1002", "ACC-1001", 403)] 
    [InlineData("ACC-1001", "ACC-9999", 404)] 
    public async Task Validation_order_source_exists_then_owner_then_destination_exists(
        string source, string destination, int expectedStatus)
    {
        var response = await _api.TransferAsync("CUST-01", source, destination, 10m);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedStatus == 404) Assert.Equal("ACCOUNT_NOT_FOUND", await ApiFixture.CodeOf(response));
        Assert.Equal(0, await _api.MovementCountAsync());
    }

    [Theory]
    [InlineData("{ esto no es json")]                                          
    [InlineData("{\"sourceAccountId\":\"ACC-1001\",\"destinationAccountId\":\"ACC-1002\",\"amount\":\"abc\"}")] 
    [InlineData("")]                                                             
    public async Task Malformed_json_or_invalid_types_return_uniform_400_invalid_request(string rawBody)
    {
        var response = await _api.SendAsync("CUST-01", HttpMethod.Post, "/api/transfers", idempotencyKey: Guid.NewGuid().ToString(),
            tweak: r => r.Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("INVALID_REQUEST", await ApiFixture.CodeOf(response));
        var body = await ApiFixture.JsonOf(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain("LineNumber", body.ToString());
        Assert.Equal(0, await _api.MovementCountAsync());
    }

    [Fact]
    public async Task E11_clients_only_touch_their_own_accounts_and_auditors_cannot_transfer()
    {
        var foreignSource = await _api.TransferAsync("CUST-01", "ACC-1002", "ACC-1001", 10m);
        var foreignMovements = await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1002/movements");
        var foreignBalance = await _api.SendAsync("CUST-01", HttpMethod.Get, "/api/accounts/ACC-1002/balance");
        var auditorTransfer = await _api.TransferAsync("AUDITOR", "ACC-1001", "ACC-1002", 10m);

        Assert.Equal(HttpStatusCode.Forbidden, foreignSource.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignMovements.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignBalance.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, auditorTransfer.StatusCode);
        Assert.Equal(250m, await _api.BalanceAsync("ACC-1002")); 
    }

    [Fact]
    public async Task Auditor_can_read_everything_and_anonymous_gets_401()
    {
        var auditorMovements = await _api.SendAsync("AUDITOR", HttpMethod.Get, "/api/accounts/ACC-1002/movements");
        using var anonymous = _api.CreateClient();
        var noToken = await anonymous.GetAsync("/api/accounts/ACC-1001/balance");

        Assert.Equal(HttpStatusCode.OK, auditorMovements.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public async Task E12_failure_between_debit_and_credit_rolls_everything_back()
    {
        var response = await _api.TransferAsync("CUST-01", "ACC-1001", "ACC-1002", 1000m,
            tweak: request => request.Headers.Add("X-Simulate-Failure", "after-debit"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Falla simulada", text); 
        Assert.Equal(10000m, await _api.BalanceAsync("ACC-1001"));
        Assert.Equal(250m, await _api.BalanceAsync("ACC-1002"));
        Assert.Equal(0, await _api.MovementCountAsync());
    }
}
