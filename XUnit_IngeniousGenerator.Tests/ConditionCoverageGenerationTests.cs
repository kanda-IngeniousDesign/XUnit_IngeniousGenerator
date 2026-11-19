using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 条件の網羅（判定ごとの成立・不成立、複合条件の要素ごとの変化）を目指して入力を加え、実際にビルド・実行して成功することまで確かめる
/// </summary>
public class ConditionCoverageGenerationTests
{
    [Fact]
    public async Task Generate_CompoundConditions_AddsInputsForEachElement()
    {
        const string source = """
            namespace Sample;
            public class Rules
            {
                public int Discount(int age, int count, bool member)
                {
                    if ((age >= 65 || member) && count > 3) return 20;
                    if (age < 13 && !member) return 10;
                    return 0;
                }

                public string Level(int score, int bonus)
                {
                    return score + bonus > 100 && score > 50 ? "high" : "low";
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Rules");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // member だけを変えて結果が変わる組（年齢が65未満でも、会員なら割引）
        Assert.Contains("[InlineData(1, 4, true, 20)]", result.Code);
        Assert.Contains("[InlineData(1, 4, false, 10)]", result.Code);

        // 引数1つの境界値では届かない、2つの引数の和と比べる条件の成立
        Assert.Contains("\"high\")]", result.Code);
        Assert.Equal(["Discount_BoundaryValues_ReturnsExpected", "Level_BoundaryValues_ReturnsExpected"], passed);
    }
}
