namespace MiniCore.Domain.Models;

public sealed record Caller(string ClientId, bool IsOperator, bool IsAuditor)
{
    public bool CanReadAll => IsAuditor || IsOperator;
    public bool CanReadAllAccounts => IsAuditor;
}

//ENTRADA
public sealed record TransferRequest(
    string? SourceAccountId,
    string? DestinationAccountId,
    decimal Amount,
    string? Currency,
    string? Description);

//SALIDA

public sealed record TransferResult(
    Guid TransferId,
    string Status,
    decimal Amount,
    decimal Fee,
    decimal TotalDebited,
    decimal SourceBalanceAfter,
    DateTime CreatedAtUtc);

public sealed record ReversalResult(
    Guid TransferId,
    string Status,
    decimal AmountReturnedToSource,
    decimal SourceBalanceAfter,
    decimal DestinationBalanceAfter,
    DateTime ReversedAtUtc);

public sealed record TransferDetail(
    Guid TransferId,
    string SourceAccountId,
    string DestinationAccountId,
    decimal Amount,
    decimal Fee,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? ReversedAtUtc,
    string? Description);

public sealed record MovementItem(
    long Id,
    Guid TransferId,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    DateTime CreatedAtUtc);

public sealed record MovementsPage(
    string AccountId,
    int Page,
    int Size,
    IReadOnlyList<MovementItem> Items);

public sealed record BalanceResult(string AccountId, decimal Balance, string Currency);
