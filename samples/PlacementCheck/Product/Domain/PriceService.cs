namespace Product.Domain;

public interface IRateProvider
{
    decimal GetRate(string currency);
}

public class PriceService
{
    private readonly IRateProvider _rates;

    public PriceService(IRateProvider rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        _rates = rates;
    }

    public decimal Convert(decimal amount, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentException.ThrowIfNullOrEmpty(currency);
        return Math.Round(amount * _rates.GetRate(currency), 2);
    }

    public string Label(string currency) => currency.ToUpperInvariant() + " / " + DateTime.Now.Year;
}
