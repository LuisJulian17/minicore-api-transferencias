using MiniCore.Domain;

namespace MiniCore.Tests;

public class DomainTests
{
    // InlineData("monto de la transferencia", "comisión esperada")
    [Theory]
    [InlineData("100.00", "0.00")]
    [InlineData("500.00", "0.00")] 
    [InlineData("500.01", "3.00")] 
    [InlineData("501.00", "3.00")]
    [InlineData("1000.00", "5.00")]
    [InlineData("1234.57", "6.17")] 
    [InlineData("1001.00", "5.01")] 

    public void Fee_follows_the_rule(string amount, string expectedFee)
    {
        var fee = FeeCalculator.Calculate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expectedFee, System.Globalization.CultureInfo.InvariantCulture), fee);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.001")]
    [InlineData("0.001")]
   public void Invalid_amounts_are_rejected(string amount)
    {
        var ex = Assert.Throws<DomainException>(() =>
            TransferRules.ValidateAmount(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal("INVALID_AMOUNT", ex.Code);
        Assert.Equal(422, ex.StatusCode);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("10.50")]
    [InlineData("5000")]
    public void Valid_amounts_are_accepted(string amount) =>
        TransferRules.ValidateAmount(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Daily_limit_is_inclusive()
    {
        Assert.False(TransferRules.ExceedsDailyLimit(3000m, 2000m)); 
        Assert.True(TransferRules.ExceedsDailyLimit(5000m, 0.01m));
        Assert.True(TransferRules.ExceedsDailyLimit(3000m, 2000.01m));
    }
    [Fact]
    public void Day_window_is_the_utc_calendar_day()
    {
        var (start, end) = TransferRules.DayWindowUtc(new DateTime(2026, 10, 10, 23, 59, 59, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc), end);
    }

    [Fact]
    public void Reversal_window_is_24_hours_after_creation()
    {
        var created = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(TransferRules.IsWithinReversalWindow(created, created.AddHours(23)));
        Assert.True(TransferRules.IsWithinReversalWindow(created, created.AddHours(24)));  
        Assert.False(TransferRules.IsWithinReversalWindow(created, created.AddHours(24).AddSeconds(1)));
    }

    [Fact]
    public void Currency_must_be_usd_when_provided()
    {
        TransferRules.ValidateCurrency("USD");
        TransferRules.ValidateCurrency(null);
        Assert.Throws<DomainException>(() => TransferRules.ValidateCurrency("EUR"));
    }

}