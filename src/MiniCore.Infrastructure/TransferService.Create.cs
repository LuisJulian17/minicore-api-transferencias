using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MiniCore.Domain;

namespace MiniCore.Infrastructure;

public sealed partial class TransferService
{
    
    public async Task<ApiResponse> CreateAsync(Caller caller, string? idempotencyKey, TransferRequest request,
        string correlationId, CancellationToken ct = default)
    {
        RequireKey(idempotencyKey);
        var key = idempotencyKey!.Trim();

        // Solo los clientes pueden crear transferencias
        if (caller.IsAuditor || caller.IsOperator) throw Errors.Forbidden();

        var sourceId = NormalizeAccountId(request.SourceAccountId);
        var destId = NormalizeAccountId(request.DestinationAccountId);
        if (sourceId.Length == 0 || destId.Length == 0)
            throw Errors.InvalidRequest("Se requieren la cuenta origen y la cuenta destino.");

        TransferRules.ValidateAmount(request.Amount);
        TransferRules.ValidateCurrency(request.Currency);
        if (sourceId == destId) throw Errors.SameAccount();

        var description = request.Description?.Trim();
        if (description is { Length: > 200 })
            throw Errors.InvalidRequest("La descripción no puede superar 200 caracteres.");

        var requestHash = Hash(sourceId, destId, Money(request.Amount), Policy.Currency, description ?? "");
        var now = UtcNow();

        await using var cn = await _db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            //Idempotencia: reservar la clave. Si ya existe, se repite la respuesta original (o error si cambio el contenido).
            if (!await TryReserveKeyAsync(cn, tx, caller.ClientId, key, requestHash, now))
            {
                await SafeRollbackAsync(tx);
                return await ReplayAsync(cn, caller.ClientId, key, requestHash);
            }

            // Bloquear las dos cuentas en orden fijo y leer sus saldos ya bloqueados.
            var (source, destination) = await LockPairAsync(cn, tx, sourceId, destId);

            //No existe la cuenta origen
            if (source is null) throw Errors.AccountNotFound();
            //No es el propietario de la cuenta origen
            if (!string.Equals(source.OwnerId, caller.ClientId, StringComparison.Ordinal))
                throw Errors.Forbidden();
            //No existe la cuenta destino
            if (destination is null) throw Errors.AccountNotFound();

            if (source.Status != "Active" || destination.Status != "Active")
                throw Errors.AccountNotActive();

            var fee = FeeCalculator.Calculate(request.Amount);
            var totalDebited = request.Amount + fee;
            if (source.Balance < totalDebited) throw Errors.InsufficientFunds();

            var (dayStart, dayEnd) = TransferRules.DayWindowUtc(now);
            var alreadySent = await cn.ExecuteScalarAsync<decimal>(
                @"SELECT COALESCE(SUM(Amount), 0)
                  FROM dbo.Transfers
                  WHERE SourceAccountId = @sourceId AND Status = N'Completed'
                    AND CreatedAtUtc >= @dayStart AND CreatedAtUtc < @dayEnd;",
                new { sourceId = source.Id, dayStart, dayEnd }, tx);
            if (TransferRules.ExceedsDailyLimit(alreadySent, request.Amount))
                throw Errors.DailyLimitExceeded();

            var transferId = Guid.NewGuid();
            await cn.ExecuteAsync(
                @"INSERT INTO dbo.Transfers
                    (Id, SourceAccountId, DestinationAccountId, Amount, Fee, Status, CreatedAtUtc, IdempotencyKey, Description)
                  VALUES (@transferId, @sourceId, @destId, @amount, @fee, N'Completed', @now, @key, @description);",
                new { transferId, sourceId = source.Id, destId = destination.Id, amount = request.Amount, fee, now, key, description }, tx);

            var sourceBalanceAfter = source.Balance - totalDebited;
            await SetBalanceAsync(cn, tx, source.Id, sourceBalanceAfter);
            await InsertMovementAsync(cn, tx, source.Id, transferId, "Debit", request.Amount, source.Balance - request.Amount, now);
            if (fee > 0)
                await InsertMovementAsync(cn, tx, source.Id, transferId, "Fee", fee, sourceBalanceAfter, now);

            _faults.AfterDebit(); //E12 Prueba

            var destinationBalanceAfter = destination.Balance + request.Amount;
            await SetBalanceAsync(cn, tx, destination.Id, destinationBalanceAfter);
            await InsertMovementAsync(cn, tx, destination.Id, transferId, "Credit", request.Amount, destinationBalanceAfter, now);

            await InsertAuditAsync(cn, tx, caller.ClientId, "TransferCreated", transferId.ToString(), correlationId,
                $"amount={Money(request.Amount)} fee={Money(fee)}", now);

            var response = Respond(201, new TransferResult(
                transferId, "Completed", request.Amount, fee, totalDebited, sourceBalanceAfter, now));
            await SaveResponseAsync(cn, tx, caller.ClientId, key, response);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Transferencia {TransferId} completada. Monto {Amount} Comision {Fee}",
                transferId, request.Amount, fee);
            return response;

        }
        catch
        {
            await SafeRollbackAsync(tx);
            throw;
        }
    }

   private static async Task SafeRollbackAsync(SqlTransaction tx)
    {
        try { await tx.RollbackAsync(); }
        catch (InvalidOperationException) { /* ya estaba finalizada */ }
        catch (SqlException) { /* la conexion se cayo: el servidor ya revirtio */ }
    }

}
