using Dapper;
using MiniCore.Domain;

namespace MiniCore.Infrastructure;

public interface IAccountQueries
{
    Task<BalanceResult> GetBalanceAsync(Caller caller, string accountId, CancellationToken ct = default);

    Task<MovementsPage> GetMovementsAsync(Caller caller, string accountId, DateTime? fromUtc, DateTime? toUtc,
        int page, int size, CancellationToken ct = default);
}

public sealed class AccountQueries : IAccountQueries
{
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;

    private sealed class AccountInfo
    {
        public string Id { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public decimal Balance { get; set; }
        public string Currency { get; set; } = "";
    }

    private sealed class MovementRow
    {
        public long Id { get; set; }
        public Guid TransferId { get; set; }
        public string Type { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal BalanceAfter { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    private readonly SqlConnectionFactory _db;

    public AccountQueries(SqlConnectionFactory db) => _db = db;

    public async Task<BalanceResult> GetBalanceAsync(Caller caller, string accountId, CancellationToken ct = default)
    {
        await using var cn = await _db.OpenAsync(ct);
        var account = await LoadAuthorizedAsync(cn, caller, accountId);
        return new BalanceResult(account.Id, account.Balance, account.Currency);
    }

    public async Task<MovementsPage> GetMovementsAsync(Caller caller, string accountId, DateTime? fromUtc,
        DateTime? toUtc, int page, int size, CancellationToken ct = default)
    {
        if (page < 1 || page > MaxPage)
            throw Errors.InvalidRequest($"page debe estar entre 1 y {MaxPage}.");
        if (size < 1 || size > MaxPageSize)
            throw Errors.InvalidRequest($"size debe estar entre 1 y {MaxPageSize}.");
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            throw Errors.InvalidRequest("from debe ser anterior a to.");

        await using var cn = await _db.OpenAsync(ct);
        var account = await LoadAuthorizedAsync(cn, caller, accountId);

        var sql = new System.Text.StringBuilder(
            @"SELECT Id, TransferId, Type, Amount, BalanceAfter, CreatedAtUtc
              FROM dbo.Movements
              WHERE AccountId = @accountId");
        var args = new DynamicParameters();
        args.Add("accountId", account.Id);

        if (fromUtc is not null)
        {
            sql.Append(" AND CreatedAtUtc >= @fromUtc");
            args.Add("fromUtc", ToUtc(fromUtc.Value));
        }
        if (toUtc is not null)
        {
            sql.Append(" AND CreatedAtUtc < @toUtc");
            args.Add("toUtc", ToUtc(toUtc.Value));
        }

        sql.Append(" ORDER BY CreatedAtUtc DESC, Id DESC OFFSET @offset ROWS FETCH NEXT @size ROWS ONLY;");
        args.Add("offset", (long)(page - 1) * size);
        args.Add("size", size);

        var rows = await cn.QueryAsync<MovementRow>(sql.ToString(), args);
        var items = rows
            .Select(r => new MovementItem(r.Id, r.TransferId, r.Type, r.Amount, r.BalanceAfter, r.CreatedAtUtc))
            .ToList();

        return new MovementsPage(account.Id, page, size, items);
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static async Task<AccountInfo> LoadAuthorizedAsync(Microsoft.Data.SqlClient.SqlConnection cn,
        Caller caller, string accountId)
    {
        var id = (accountId ?? "").Trim().ToUpperInvariant();
        var account = await cn.QuerySingleOrDefaultAsync<AccountInfo>(
            "SELECT Id, OwnerId, Balance, Currency FROM dbo.Accounts WHERE Id = @id;", new { id });

        if (account is null) throw Errors.AccountNotFound();

        if (!caller.CanReadAllAccounts && !string.Equals(account.OwnerId, caller.ClientId, StringComparison.Ordinal))
            throw Errors.Forbidden();

        return account;
    }
}
