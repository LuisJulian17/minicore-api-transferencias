using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MiniCore.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokens = new();

    public ApiFixture()
    {
        var connection = Environment.GetEnvironmentVariable("MINICORE_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection))
            Environment.SetEnvironmentVariable("ConnectionStrings__MiniCore", connection);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public string ConnectionString =>
        Environment.GetEnvironmentVariable("MINICORE_TEST_CONNECTION")
        ?? Services.GetRequiredService<IConfiguration>().GetConnectionString("MiniCore")!;

    public async Task ResetAsync()
    {
        Clock.Set(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero));

        var script = await File.ReadAllTextAsync(FindSeedScript());
        await using var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync();

        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = new SqlCommand(batch, cn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string FindSeedScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "02_seed_and_reset.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("No se encontro db/02_seed_and_reset.sql subiendo desde el directorio de pruebas.");
    }

    private static IEnumerable<string> SplitBatches(string script)
    {
        var current = new StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.ToString().Trim().Length > 0) yield return current.ToString();
                current.Clear();
            }
            else if (!trimmed.StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
            {
                current.AppendLine(line);
            }
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }


    public async Task<string> TokenAsync(string user)
    {
        if (_tokens.TryGetValue(user, out var cached)) return cached;

        using var anon = CreateClient();
        var json = await anon.GetFromJsonAsync<JsonElement>($"/dev/token/{user}");
        var token = json.GetProperty("token").GetString()!;
        _tokens[user] = token;
        return token;
    }

    public async Task<HttpResponseMessage> SendAsync(string user, HttpMethod method, string url,
        object? body = null, string? idempotencyKey = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(user));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        tweak?.Invoke(request);
        return await client.SendAsync(request);
    }

    public Task<HttpResponseMessage> TransferAsync(string user, string source, string destination, decimal amount,
        string? key = "auto", string? description = null, Action<HttpRequestMessage>? tweak = null)
    {
        if (key == "auto") key = Guid.NewGuid().ToString();
        return SendAsync(user, HttpMethod.Post, "/api/transfers",
            new { sourceAccountId = source, destinationAccountId = destination, amount, currency = "USD", description },
            key, tweak);
    }

    public async Task<decimal> BalanceAsync(string accountId)
    {
        var response = await SendAsync("AUDITOR", HttpMethod.Get, $"/api/accounts/{accountId}/balance");
        response.EnsureSuccessStatusCode();
        return (await JsonOf(response)).GetProperty("balance").GetDecimal();
    }

    public async Task<int> MovementCountAsync()
    {
        await using var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Movements;", cn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var json = await JsonOf(response);
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<ApiFixture> { }

public sealed class TestClock : TimeProvider
{
    private long _utcTicks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}
