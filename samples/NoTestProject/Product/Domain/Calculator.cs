namespace Product.Domain;

public class Calculator
{
    public int Add(int a, int b) => a + b;

    public int Divide(int dividend, int divisor)
    {
        if (divisor == 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        return dividend / divisor;
    }

    public int Percent(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
        return value;
    }

    public string Judge(int score) => score switch
    {
        >= 80 => "合格",
        >= 60 => "再試験",
        _ => "不合格",
    };

    public decimal Discount(decimal price, bool member)
    {
        if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
        var rate = member ? 0.9m : 1.0m;
        if (price >= 10000) rate -= 0.05m;
        return price * rate;
    }

    public string Format(string label, int digits)
    {
        if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required", nameof(label));
        var text = label.Trim();
        if (digits < 0) throw new ArgumentOutOfRangeException(nameof(digits));
        return text;
    }
}
