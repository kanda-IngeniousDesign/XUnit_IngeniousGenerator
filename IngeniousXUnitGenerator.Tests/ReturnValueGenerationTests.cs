using IngeniousXUnitGenerator.Tests.Support;

namespace IngeniousXUnitGenerator.Tests;

/// <summary>
/// 戻り値テスト（境界値の入力と、静的に計算した期待値）を生成し、実際に実行して成功することまで確かめる
/// </summary>
public class ReturnValueGenerationTests
{
    [Fact]
    public async Task Generate_SwitchExpressionWithRelationalPatterns_UsesBoundariesAndConstants()
    {
        const string source = """
            namespace Sample;
            public class Grader
            {
                public string Judge(int score) => score switch
                {
                    >= 80 => "A",
                    >= 60 and < 80 => "B",
                    _ => "C",
                };
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Grader");

        Assert.Contains("[InlineData(59, \"C\")]", result.Code);
        Assert.Contains("[InlineData(60, \"B\")]", result.Code);
        Assert.Contains("[InlineData(79, \"B\")]", result.Code);
        Assert.Contains("[InlineData(80, \"A\")]", result.Code);
        Assert.Contains("public void Judge_BoundaryValues_ReturnsExpected(int score, string expected)", result.Code);
    }

    [Fact]
    public async Task Generate_DecimalCalculation_UsesTheoryDataWithComputedValues()
    {
        const string source = """
            using System;
            namespace Sample;
            public class PriceCalculator
            {
                public decimal Discount(decimal price, bool member)
                {
                    if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
                    var rate = member ? 0.9m : 1.0m;
                    if (price >= 10000) rate -= 0.05m;
                    return price * rate;
                }
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.PriceCalculator");

        Assert.Contains("public static TheoryData<decimal, bool, decimal> Discount_BoundaryValues", result.Code);
        Assert.Contains("{ 10000M, false, 9500M },", result.Code);
        Assert.Contains("{ 10001M, false, 9500.95M },", result.Code);
        Assert.Contains("{ 1M, true, 0.9M }", result.Code);
        Assert.Contains("[MemberData(nameof(Discount_BoundaryValues))]", result.Code);
    }

    [Fact]
    public async Task Generate_ArithmeticOnLongAndDouble_RendersTypedLiterals()
    {
        const string source = """
            namespace Sample;
            public static class MathHelper
            {
                public static long Square(long value) => value * value;

                public static double Half(double value) => value / 2;

                public static int Clamp(int value)
                {
                    var result = value;
                    if (value < 0) result = 0;
                    else if (value > 10) result = 10;
                    return result;
                }
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.MathHelper");

        Assert.Contains("[InlineData(-1L, 1L)]", result.Code);
        Assert.Contains("[InlineData(1D, 0.5D)]", result.Code);
        Assert.Contains("[InlineData(11, 10)]", result.Code);
        Assert.Contains("[InlineData(-1, 0)]", result.Code);
        Assert.Contains("MathHelper.Square(value)", result.Code);
    }

    [Fact]
    public async Task Generate_StringConditionsAndConcatenation_TestsPass()
    {
        const string source = """
            namespace Sample;
            public class Labeler
            {
                public string Label(string name, int count)
                {
                    if (string.IsNullOrEmpty(name)) return "none";
                    if (name == "admin") return "ADMIN";
                    return name + ":" + count + "/" + name.Length;
                }
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Labeler");

        Assert.Contains("[InlineData(\"a\", 1, \"a:1/1\")]", result.Code);
        Assert.Contains("[InlineData(\"\", 1, \"none\")]", result.Code);
        Assert.Contains("[InlineData(\"admin\", 1, \"ADMIN\")]", result.Code);
    }

    [Fact]
    public async Task Generate_InputThatThrowsInBody_IsLeftOutOfReturnValueRows()
    {
        const string source = """
            namespace Sample;
            public class Ratio
            {
                public int Of(int a, int b) => a / b;
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Ratio");

        Assert.Contains("[InlineData(1, -1, -1)]", result.Code);
        Assert.DoesNotContain("[InlineData(1, 0,", result.Code);
    }

    [Fact]
    public async Task Generate_ThrowHelperGuard_UsesGuardConstantAsBoundary()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Meter
            {
                public int Percent(int value)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(value);
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
                    return value * 2;
                }
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Meter");

        Assert.Contains("[InlineData(0, 0)]", result.Code);
        Assert.Contains("[InlineData(99, 198)]", result.Code);
        Assert.Contains("[InlineData(100, 200)]", result.Code);
        Assert.DoesNotContain("[InlineData(101,", result.Code);
        Assert.DoesNotContain("[InlineData(-1,", result.Code);
    }

    [Theory]
    [InlineData("public decimal Rate(decimal price) => price * _rate;", "フィールド・プロパティ '_rate' の参照")]
    [InlineData("public int Sum(int count) { var total = 0; for (var i = 0; i < count; i++) total += i; return total; }", "ループ")]
    [InlineData("public string Show(int value) => value.ToString();", "メソッド呼び出し 'value.ToString'")]
    [InlineData("public string Price(double value) => \"\\\\\" + value;", "カルチャに依存する文字列変換")]
    [InlineData("public string Text(int value) => $\"{value}件\";", "文字列補間")]
    public void Generate_UnsupportedSyntax_ReportsReason(string method, string reason)
    {
        var source = $$"""
            namespace Sample;
            public class Target
            {
                private readonly decimal _rate = 0.9m;

                {{method}}
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Target");

        Assert.Equal(0, result.GeneratedTestCount);
        var item = Assert.Single(result.ManualItems);
        Assert.Contains("戻り値テスト: 戻り値を静的に計算できません", item);
        Assert.Contains(reason, item);
    }

    [Fact]
    public void Generate_UnsupportedParameterType_ReportsType()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Target
            {
                public int Days(DateTime from) => 1;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Target");

        var item = Assert.Single(result.ManualItems);
        Assert.StartsWith("Days(DateTime): 戻り値テスト: 戻り値を静的に計算できません（引数 from の型 DateTime は計算の対象外）", item);
        Assert.EndsWith("テストプロジェクトに Verify.Xunit を追加すると、実行結果を記録するテストを生成します", item);
    }

    private static async Task<Core.GenerationResult> GenerateAndRunAsync(string source, string metadataName)
    {
        var result = TargetCompiler.Generate(source, metadataName);
        Assert.Empty(result.ManualItems);

        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);
        Assert.Equal(result.GeneratedTestCount, passed.Count);
        return result;
    }
}
