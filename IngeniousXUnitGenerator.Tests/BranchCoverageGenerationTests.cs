using IngeniousXUnitGenerator.Tests.Support;

namespace IngeniousXUnitGenerator.Tests;

/// <summary>
/// 分岐網羅の拡充（switch 文・引数どうしの比較・文字列操作・丸め）を、静的に計算した期待値で生成し、実際に実行して確かめる
/// </summary>
public class BranchCoverageGenerationTests
{
    [Fact]
    public async Task Generate_SwitchStatement_UsesCaseConstantsAndComputesEachBranch()
    {
        const string source = """
            namespace Sample;
            public class Shipping
            {
                public int Fee(int zone)
                {
                    switch (zone)
                    {
                        case 1:
                            return 500;
                        case 2:
                        case 3:
                            return 800;
                        case >= 10:
                            return 2000;
                        default:
                            break;
                    }

                    return 1000;
                }
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Shipping");

        Assert.Contains("[InlineData(1, 500)]", result.Code);
        Assert.Contains("[InlineData(3, 800)]", result.Code);
        Assert.Contains("[InlineData(4, 1000)]", result.Code);
        Assert.Contains("[InlineData(10, 2000)]", result.Code);
        Assert.Contains("[InlineData(9, 1000)]", result.Code);
    }

    [Fact]
    public async Task Generate_ComparisonBetweenParameters_AddsInputsAroundTheOtherParameter()
    {
        const string source = """
            namespace Sample;
            public class Range
            {
                public string Compare(int value, int limit)
                {
                    if (value > limit) return "over";
                    if (value == limit) return "just";
                    return "under";
                }
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Range");

        Assert.Contains("[InlineData(1, 1, \"just\")]", result.Code);
        Assert.Contains("[InlineData(2, 1, \"over\")]", result.Code);
        Assert.Contains("[InlineData(1, 2, \"under\")]", result.Code);
    }

    [Fact]
    public async Task Generate_CultureIndependentStringAndRoundingOperations_AreComputedStatically()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Formatter
            {
                public string Code(string name)
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length == 0) return "(empty)";
                    return trimmed.ToUpperInvariant().PadLeft(4, '0').Substring(0, 4);
                }

                public bool IsAdmin(string name) => name.StartsWith("admin", StringComparison.Ordinal) || name.Contains('@');

                public decimal Tax(decimal price) => Math.Round(price * 0.1m, 0);
            }
            """;

        var result = await GenerateAndRunAsync(source, "Sample.Formatter");

        Assert.Contains("public void Code_BoundaryValues_ReturnsExpected(string name, string expected)", result.Code);
        Assert.Contains("[InlineData(\"a\", \"000A\")]", result.Code);
        Assert.Contains("[InlineData(\" \", \"(empty)\")]", result.Code);
        Assert.Contains("public void IsAdmin_BoundaryValues_ReturnsExpected(string name, bool expected)", result.Code);
        Assert.Contains("public void Tax_BoundaryValues_ReturnsExpected(decimal price, decimal expected)", result.Code);
    }

    [Fact]
    public void Generate_CultureDependentStringOperation_IsNotComputedStatically()
    {
        const string source = """
            namespace Sample;
            public class Formatter
            {
                public string Upper(string name) => name.ToUpper();
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Formatter");

        Assert.Equal(0, result.GeneratedTestCount);
        Assert.Contains("メソッド呼び出し 'name.ToUpper'", Assert.Single(result.ManualItems));
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
