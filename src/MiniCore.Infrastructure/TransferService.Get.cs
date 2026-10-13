using Dapper;
using MiniCore.Domain;

namespace MiniCore.Infrastructure;

public sealed partial class TransferService
{
    public async Task<TransferDetail> GetAsync(Caller caller, Guid transferId, CancellationToken ct = default)
    {
        await using var cn = await _db.OpenAsync(ct);

        var row = await cn.QuerySingleOrDefaultAsync<TransferRow>(
            @"SELECT Id, SourceAccountId, DestinationAccountId, Amount, Fee, Status,
                     CreatedAtUtc, ReversedAtUtc, Description
              FROM dbo.Transfers WHERE Id = @transferId;",
            new { transferId });

        if (row is null) throw Errors.TransferNotFound();

        if (!caller.CanReadAll)
        {
            var owners = (await cn.QueryAsync<string>(
                "SELECT OwnerId FROM dbo.Accounts WHERE Id IN (@source, @destination);",
                new { source = row.SourceAccountId, destination = row.DestinationAccountId })).ToList();

            if (!owners.Contains(caller.ClientId, StringComparer.Ordinal))
                throw Errors.Forbidden();
        }

        return new TransferDetail(row.Id, row.SourceAccountId, row.DestinationAccountId, row.Amount, row.Fee,
            row.Status, row.CreatedAtUtc, row.ReversedAtUtc, row.Description);
    }
}
