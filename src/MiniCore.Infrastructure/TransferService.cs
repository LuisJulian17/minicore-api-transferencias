using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MiniCore.Domain;

namespace MiniCore.Infrastructure;

public sealed record ApiResponse(int StatusCode, string JsonBody, bool Replayed);

public interface ITransferService
{
    Task<ApiResponse> CreateAsync(Caller caller, string? idempotencyKey, TransferRequest request,
        string correlationId, CancellationToken ct = default);

    Task<ApiResponse> ReverseAsync(Caller caller, Guid transferId, string? idempotencyKey,
        string correlationId, CancellationToken ct = default);

    Task<TransferDetail> GetAsync(Caller caller, Guid transferId, CancellationToken ct = default);

}

public sealed partial class TransferService : ITransferService
{
    private const int MaxIdempotencyKeyLength = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    
    private readonly SqlConnectionFactory _db;
    private readonly TimeProvider _time;
      private readonly ITransferFaultInjector _faults;
    private readonly ILogger<TransferService> _logger;

    public TransferService(SqlConnectionFactory db, TimeProvider time, ITransferFaultInjector faults, ILogger<TransferService> logger)
    {
        _db = db;
        _time = time;
        _faults = faults;
        _logger = logger;
    }

// Filas de la base de datos
    public sealed class AccountRow
    {
        public string Id { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Balance { get; set; }
        public string Currency { get; set; } = ""; 
    }
    private sealed class TransferRow
    {
        public Guid Id { get; set; }
        public string SourceAccountId { get; set; } = "";
        public string DestinationAccountId { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal Fee { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ReversedAtUtc { get; set; }
        public string? Description { get; set; }
    }
        private sealed class IdempotencyRow
    {
        public string RequestHash { get; set; } = "";
        public int? ResponseStatus { get; set; }
        public string? ResponseBody { get; set; }
    }

// Utilidades 
    private DateTime UtcNow()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
    private static string NormalizeAccountId(string? id) => (id ?? "").Trim().ToUpperInvariant();

    private static string Hash(params string[] parts)
    {
        var text = string.Join("|", parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static ApiResponse Respond(int statusCode, object body) =>
        new(statusCode, JsonSerializer.Serialize(body, Json), Replayed: false);

    // Bloquea una cuenta para actualización dentro de una transacción SQL si dos transacciones intentan modificarla simultáneamente.
    private static Task<AccountRow?> LockAccountAsync(SqlConnection cn, SqlTransaction tx, string id) =>
        cn.QuerySingleOrDefaultAsync<AccountRow>(
            @"SELECT Id, OwnerId, Status, Balance, Currency
              FROM dbo.Accounts WITH (UPDLOCK, ROWLOCK)
              WHERE Id = @id;",
            new { id }, tx);


    //Bloque de cuentas en orden si A transfiere a B y B transfiere a A llegan y a la vez evita deadlocks circular
    private static async Task<(AccountRow? A, AccountRow? B)> LockPairAsync(
        SqlConnection cn, SqlTransaction tx, string idA, string idB)
    {
        var aFirst = string.CompareOrdinal(idA, idB) <= 0;
        var first = await LockAccountAsync(cn, tx, aFirst ? idA : idB);
        var second = await LockAccountAsync(cn, tx, aFirst ? idB : idA);
        return aFirst ? (first, second) : (second, first);
    }

    private static Task InsertMovementAsync(SqlConnection cn, SqlTransaction tx, string accountId, Guid transferId,
        string type, decimal amount, decimal balanceAfter, DateTime now) =>
        cn.ExecuteAsync(
            @"INSERT INTO dbo.Movements (AccountId, TransferId, Type, Amount, BalanceAfter, CreatedAtUtc)
              VALUES (@accountId, @transferId, @type, @amount, @balanceAfter, @now);",
            new { accountId, transferId, type, amount, balanceAfter, now }, tx);

    private static Task SetBalanceAsync(SqlConnection cn, SqlTransaction tx, string accountId, decimal newBalance) =>
        cn.ExecuteAsync("UPDATE dbo.Accounts SET Balance = @newBalance WHERE Id = @accountId;",
            new { accountId, newBalance }, tx);

    private static Task InsertAuditAsync(SqlConnection cn, SqlTransaction tx, string actorId, string action,
        string entityId, string correlationId, string details, DateTime now) =>
        cn.ExecuteAsync(
            @"INSERT INTO dbo.AuditLog (TimestampUtc, ActorId, Action, EntityId, CorrelationId, Details)
              VALUES (@now, @actorId, @action, @entityId, @correlationId, @details);",
            new { now, actorId, action, entityId, correlationId, details }, tx);

    // Reserva la clave dentro de la transacción: si otra petición ya la tiene, espera y devuelve false si esa se confirma.
    private static async Task<bool> TryReserveKeyAsync(SqlConnection cn, SqlTransaction tx,
        string clientId, string key, string requestHash, DateTime now)
    {
        try
        {
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.IdempotencyKeys (ClientId, [Key], RequestHash, CreatedAtUtc)
                  VALUES (@clientId, @key, @requestHash, @now);",
                new { clientId, key, requestHash, now }, tx);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601) 
        {
            return false;
        }
    }

    private static Task SaveResponseAsync(SqlConnection cn, SqlTransaction tx,
        string clientId, string key, ApiResponse response) =>
        cn.ExecuteAsync(
            @"UPDATE dbo.IdempotencyKeys
              SET ResponseStatus = @status, ResponseBody = @body
              WHERE ClientId = @clientId AND [Key] = @key;",
            new { clientId, key, status = response.StatusCode, body = response.JsonBody }, tx);


    private static async Task<ApiResponse> ReplayAsync(SqlConnection cn, string clientId, string key, string requestHash)
    {
        var row = await cn.QuerySingleOrDefaultAsync<IdempotencyRow>(
            @"SELECT RequestHash, ResponseStatus, ResponseBody
              FROM dbo.IdempotencyKeys WHERE ClientId = @clientId AND [Key] = @key;",
            new { clientId, key });

        if (row is null || row.ResponseStatus is null || row.ResponseBody is null)
            throw Errors.InvalidRequest("La clave de idempotencia está en uso, reintenta en unos segundos.");

        if (!string.Equals(row.RequestHash, requestHash, StringComparison.Ordinal))
            throw Errors.IdempotencyKeyReused();

        return new ApiResponse(row.ResponseStatus.Value, row.ResponseBody, Replayed: true);
    }

    private static void RequireKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw Errors.MissingIdempotencyKey();
        if (key.Trim().Length > MaxIdempotencyKeyLength)
            throw Errors.InvalidRequest($"Idempotency-Key no puede superar {MaxIdempotencyKeyLength} caracteres.");
    }
}