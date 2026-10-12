namespace MiniCore.Domain;

public sealed class DomainException : Exception
{
    public string? Code { get; }
    public int StatusCode { get; }

    public DomainException(string? code, int statusCode, string message) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

public static class Errors
{
    public static DomainException MissingIdempotencyKey() =>
        new("MISSING_IDEMPOTENCY_KEY", 400, "Falta la cabecera Idempotency-Key.");

    public static DomainException InvalidAmount() =>
        new("INVALID_AMOUNT", 422, "El monto debe ser mayor a 0 y tener máximo 2 decimales.");

    public static DomainException InvalidCurrency() =>
        new("INVALID_CURRENCY", 422, "La única moneda soportada es USD.");

    public static DomainException SameAccount() =>
        new("SAME_ACCOUNT", 422, "La cuenta origen y destino deben ser distintas.");

    public static DomainException AccountNotFound() =>
        new("ACCOUNT_NOT_FOUND", 404, "Alguna de las cuentas no existe.");

    public static DomainException AccountNotActive() =>
        new("ACCOUNT_NOT_ACTIVE", 422, "Alguna de las cuentas no está activa.");

    public static DomainException InsufficientFunds() =>
        new("INSUFFICIENT_FUNDS", 422, "Saldo insuficiente para cubrir monto y comisión.");

    public static DomainException DailyLimitExceeded() =>
        new("DAILY_LIMIT_EXCEEDED", 422, "Se superaría el límite diario de transferencias.");

    public static DomainException IdempotencyKeyReused() =>
        new("IDEMPOTENCY_KEY_REUSED", 422, "La clave de idempotencia ya se usó con un contenido distinto.");

    public static DomainException TransferAlreadyReversed() =>
        new("TRANSFER_ALREADY_REVERSED", 409, "La transferencia ya fue reversada.");

    public static DomainException ReversalWindowExpired() =>
        new("REVERSAL_WINDOW_EXPIRED", 422, "Pasaron más de 24 horas desde la transferencia.");

    public static DomainException ReversalInsufficientFunds() =>
        new("REVERSAL_INSUFFICIENT_FUNDS", 409, "La cuenta destino no tiene saldo para el reverso."); 

    

    public static DomainException TransferNotFound() =>
        new("TRANSFER_NOT_FOUND", 404, "La transferencia no existe.");

    public static DomainException Forbidden() =>
        new(null, 403, "No tienes permiso para esta operación.");

    public static DomainException Unauthorized() =>
        new(null, 401, "No autenticado.");

    public static DomainException InvalidRequest(string message) =>
        new("INVALID_REQUEST", 400, message);
}