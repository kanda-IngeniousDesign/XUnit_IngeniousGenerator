// ソリューションに追加するには: dotnet sln "/Users/yujikanda/WorkSpace/IngeniousXUnitGenerator/samples/NoTestProject/NoTestProject.slnx" add "/Users/yujikanda/WorkSpace/IngeniousXUnitGenerator/samples/NoTestProject/Product.Tests/Product.Tests.csproj"
using System;
using Product.Domain;
using Xunit;

namespace Product.Tests.Domain;

public class CalculatorTests
{
    private readonly Calculator _sut = new Calculator();

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, -1, 0)]
    public void Add_BoundaryValues_ReturnsExpected(int a, int b, int expected)
    {
        Assert.Equal(expected, _sut.Add(a, b));
    }

    [Fact]
    public void Divide_DivisorIsZero_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Divide(0, 0));
        Assert.Equal("divisor", exception.ParamName);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(0, 1, 0)]
    [InlineData(-1, 1, -1)]
    [InlineData(1, -1, -1)]
    public void Divide_BoundaryValues_ReturnsExpected(int dividend, int divisor, int expected)
    {
        Assert.Equal(expected, _sut.Divide(dividend, divisor));
    }

    [Fact]
    public void Percent_ValueIsMinus1_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Percent(-1));
        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Percent_ValueIs101_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Percent(101));
        Assert.Equal("value", exception.ParamName);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    public void Percent_BoundaryValues_ReturnsExpected(int value, int expected)
    {
        Assert.Equal(expected, _sut.Percent(value));
    }

    [Theory]
    [InlineData(1, "不合格")]
    [InlineData(0, "不合格")]
    [InlineData(59, "不合格")]
    [InlineData(60, "再試験")]
    [InlineData(61, "再試験")]
    [InlineData(79, "再試験")]
    [InlineData(80, "合格")]
    [InlineData(81, "合格")]
    public void Judge_BoundaryValues_ReturnsExpected(int score, string expected)
    {
        Assert.Equal(expected, _sut.Judge(score));
    }

    [Fact]
    public void Discount_PriceIsMinus1_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Discount(-1M, false));
        Assert.Equal("price", exception.ParamName);
    }

    public static TheoryData<decimal, bool, decimal> Discount_BoundaryValues => new TheoryData<decimal, bool, decimal>
    {
        { 1M, false, 1M },
        { 0M, false, 0M },
        { 9999M, false, 9999M },
        { 10000M, false, 9500M },
        { 10001M, false, 9500.95M },
        { 1M, true, 0.9M }
    };

    [Theory]
    [MemberData(nameof(Discount_BoundaryValues))]
    public void Discount_BoundaryValues_ReturnsExpected(decimal price, bool member, decimal expected)
    {
        Assert.Equal(expected, _sut.Discount(price, member));
    }

    [Fact]
    public void Format_LabelIsNull_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.Format(null!, 0));
        Assert.Equal("label", exception.ParamName);
    }

    [Fact]
    public void Format_LabelIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.Format("", 0));
        Assert.Equal("label", exception.ParamName);
    }

    [Fact]
    public void Format_DigitsIsMinus1_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Format("a", -1));
        Assert.Equal("digits", exception.ParamName);
    }

    [Theory]
    [InlineData("a", 1, "a")]
    [InlineData(" ", 1, "")]
    [InlineData("a", 0, "a")]
    public void Format_BoundaryValues_ReturnsExpected(string label, int digits, string expected)
    {
        Assert.Equal(expected, _sut.Format(label, digits));
    }
}
