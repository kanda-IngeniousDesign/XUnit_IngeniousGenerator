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

    public string Format(string label, int digits)
    {
        if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required", nameof(label));
        var text = label.Trim();
        if (digits < 0) throw new ArgumentOutOfRangeException(nameof(digits));
        return text;
    }
}
