using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MiniCore.Domain;

namespace MiniCore.Infrastructure;

public sealed partial class TransferService
{
    public async Task<ApiResponse> ReverseAsync(Caller caller, Guid transferId, string? idempotencyKey,
        string correlationId, CancellationToken ct = default)
    {
        RequireKey(idempotencyKey);
        var key = idempotencyKey!.Trim();

        if (!caller.IsOperator) throw Errors.Forbidden();

        var requestHash = Hash("reverse", transferId.ToString("N"));
        var now = UtcNow();

        await using var cn = await _db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            if (!await TryReserveKeyAsync(cn, tx, caller.ClientId, key, requestHash, now))
            {
                await SafeRollbackAsync(tx);
                return await ReplayAsync(cn, caller.ClientId, key, requestHash);
            }

            var transfer = await cn.QuerySingleOrDefaultAsync<TransferRow>(
                @"SELECT Id, SourceAccountId, DestinationAccountId, Amount, Fee, Status,
                         CreatedAtUtc, ReversedAtUtc, Description
                  FROM dbo.Transfers WITH (UPDLOCK, ROWLOCK)
                  WHERE Id = @transferId;",
                new { transferId }, tx);

            if (transfer is null) throw Errors.TransferNotFound();
            if (transfer.Status == "Reversed") throw Errors.TransferAlreadyReversed();
            if (!TransferRules.IsWithinReversalWindow(transfer.CreatedAtUtc, now))
                throw Errors.ReversalWindowExpired();

            var (source, destination) = await LockPairAsync(cn, tx, transfer.SourceAccountId, transfer.DestinationAccountId);
            if (source is null || destination is null) throw Errors.AccountNotFound();

            if (destination.Balance < transfer.Amount) throw Errors.ReversalInsufficientFunds();

            var destinationBalanceAfter = destination.Balance - transfer.Amount;
            var refund = transfer.Amount + transfer.Fee;          // al origen vuelve el monto Y la comision
            var sourceBalanceAfter = source.Balance + refund;

            await cn.ExecuteAsync(
                "UPDATE dbo.Transfers SET Status = N'Reversed', ReversedAtUtc = @now WHERE Id = @transferId;",
                new { transferId, now }, tx);

            await SetBalanceAsync(cn, tx, destination.Id, destinationBalanceAfter);
            await InsertMovementAsync(cn, tx, destination.Id, transferId, "ReversalDebit", transfer.Amount, destinationBalanceAfter, now);

            await SetBalanceAsync(cn, tx, source.Id, sourceBalanceAfter);
            await InsertMovementAsync(cn, tx, source.Id, transferId, "ReversalCredit", transfer.Amount, source.Balance + transfer.Amount, now);
            if (transfer.Fee > 0)
                await InsertMovementAsync(cn, tx, source.Id, transferId, "FeeRefund", transfer.Fee, sourceBalanceAfter, now);

            await InsertAuditAsync(cn, tx, caller.ClientId, "TransferReversed", transferId.ToString(), correlationId,
                $"amount={Money(transfer.Amount)} fee={Money(transfer.Fee)}", now);

            var response = Respond(200, new ReversalResult(
                transferId, "Reversed", refund, sourceBalanceAfter, destinationBalanceAfter, now));
            await SaveResponseAsync(cn, tx, caller.ClientId, key, response);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Transferencia {TransferId} reversada", transferId);
            return response;
        }
        catch
        {
            await SafeRollbackAsync(tx);
            throw;
        }
    }
}
