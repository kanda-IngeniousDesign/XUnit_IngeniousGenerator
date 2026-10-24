using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

public class TestClassGeneratorTests
{
    [Fact]
    public void Generate_ClassWithoutGuards_EmitsEmptyTestClass()
    {
        var result = TargetCompiler.Generate("namespace Sample.Domain; public class Foo { }", "Sample.Domain.Foo", "Sample.Tests.Domain");

        Assert.Equal("FooTests", result.TestClassName);
        Assert.Equal(0, result.GeneratedTestCount);
        Assert.Empty(result.ManualItems);
        Assert.Equal(
            "using Sample.Domain;\r\nusing Xunit;\r\n\r\nnamespace Sample.Tests.Domain;\r\n\r\npublic class FooTests\r\n{\r\n}\r\n",
            result.Code);
    }

    [Theory]
    [InlineData("Sample.Domain")]
    [InlineData("Sample.Domain.Tests")]
    public void Generate_TargetNamespaceVisibleFromTestNamespace_OmitsUsing(string testNamespace)
    {
        var result = TargetCompiler.Generate("namespace Sample.Domain; public class Foo { }", "Sample.Domain.Foo", testNamespace);

        Assert.DoesNotContain("using Sample.Domain;", result.Code);
    }

    [Fact]
    public void Generate_NoTestNamespace_EmitsTestClassWithoutNamespace()
    {
        var result = TargetCompiler.Generate("public class Foo { }", "Foo", null);

        Assert.DoesNotContain("namespace", result.Code);
        Assert.Contains("public class FooTests", result.Code);
    }

    [Fact]
    public async Task Generate_GuardedMethod_EmitsFormattedTestFile()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Calculator
            {
                public int Divide(int dividend, int divisor)
                {
                    if (divisor == 0) throw new ArgumentOutOfRangeException(nameof(divisor));
                    return dividend / divisor;
                }

                public static int Negate(int value)
                {
                    if (value == int.MinValue) throw new OverflowException();
                    return -value;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Calculator");

        Assert.Equal(
            """
            using System;
            using Xunit;

            namespace Sample.Tests;

            public class CalculatorTests
            {
                private readonly Calculator _sut = new Calculator();

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
                public void Negate_ValueIsMinus2147483648_ThrowsOverflowException()
                {
                    Assert.Throws<OverflowException>(() => Calculator.Negate(-2147483648));
                }

                [Theory]
                [InlineData(1, -1)]
                [InlineData(-2147483647, 2147483647)]
                [InlineData(0, 0)]
                public void Negate_BoundaryValues_ReturnsExpected(int value, int expected)
                {
                    Assert.Equal(expected, Calculator.Negate(value));
                }
            }

            """.ReplaceLineEndings("\r\n"),
            result.Code);

        // int.MinValue の境界値（-2147483648）がそのままコンパイルでき、テストが成功すること
        await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);
    }
}
