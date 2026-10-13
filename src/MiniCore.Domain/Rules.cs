namespace MiniCore.Domain;

public static class Policy
{
    public const string Currency = "USD";

    public const decimal FeeFreeThreshold = 500.00m;
    public const decimal FeeRate = 0.005m; //Comision del 5%
    public const decimal MinimumFee = 3.00m; //Comision minima de 3 USD

    //limite diario por cuenta origen
    public const decimal DailyLimit = 5000.00m;
    public static readonly TimeSpan ReversalWindow = TimeSpan.FromHours(24);

    public const decimal MaxAmount = 1_000_000_000_000.00m;
}

public static class FeeCalculator
{
    public static decimal Calculate(decimal amount)
    {
        if (amount <= Policy.FeeFreeThreshold)
            return 0m;

        var fee = Math.Round(amount * Policy.FeeRate, 2, MidpointRounding.AwayFromZero);
        return Math.Max(fee, Policy.MinimumFee);
    }
}

public static class TransferRules
{
    //validacion del monto que no tenga mas de dos decimales que sea mayor de cero y no exeda em maxamount
    public static void ValidateAmount(decimal amount)
    {
        var hasMoreThanTwoDecimal = decimal.Round(amount, 2) != amount;
        if (amount <= 0m || hasMoreThanTwoDecimal || amount > Policy.MaxAmount)
            throw Errors.InvalidAmount();
    }

    //validacion de la moneda
    public static void ValidateCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return;
        if (!string.Equals(currency.Trim(), Policy.Currency, StringComparison.OrdinalIgnoreCase))
            throw Errors.InvalidCurrency();

    }

    //ventana del dia en UTC
       public static (DateTime StartUtc, DateTime EndUtc) DayWindowUtc(DateTime utcNow)
    {
        var start = DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Utc);
        return (start, start.AddDays(1));
    }
    //validacion del limite diario
    public static bool ExceedsDailyLimit(decimal alreadySent, decimal amount) =>
        alreadySent + amount > Policy.DailyLimit;

    //Validacion de la ventana de retorno de transferencias
    public static bool IsWithinReversalWindow(DateTime createdAtUtc, DateTime utcNow) =>
        utcNow - createdAtUtc <= Policy.ReversalWindow;

}
