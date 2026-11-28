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

    [Fact]
    public async Task Generate_DerivedComparisonBooleansAndDivision_FindsBoundariesPairsAndExceptions()
    {
        const string source = """
            namespace Sample;
            public class Shop
            {
                public string Rank(int score)
                {
                    var adjusted = score / 2;
                    if (adjusted >= 40) return "A";
                    if (adjusted >= 20) return "B";
                    return "C";
                }

                public int Price(int quantity, bool member, bool coupon, bool sale)
                {
                    var price = quantity * 100;
                    if (member) price -= 10;
                    if (coupon && !sale) price -= 20;
                    if (sale || quantity >= 10) price -= 5;
                    return price;
                }

                public int Divide(int a, int b)
                {
                    return a / b;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Shop");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // ローカル変数を通した比較は、引数が切り替わる境界（39 と 40、79 と 80）を探す
        Assert.Contains("[InlineData(39, \"C\")]", result.Code);
        Assert.Contains("[InlineData(40, \"B\")]", result.Code);
        Assert.Contains("[InlineData(79, \"B\")]", result.Code);
        Assert.Contains("[InlineData(80, \"A\")]", result.Code);

        // bool の引数は、どの2つの組でも true / false の4通りがそろう
        Assert.Contains("[InlineData(1, true, true, true, 85)]", result.Code);

        // 0 での割り算は、例外テストにする
        Assert.Equal(
            [
                "Rank_BoundaryValues_ReturnsExpected",
                "Price_BoundaryValues_ReturnsExpected",
                "Divide_BoundaryValues_ReturnsExpected",
                "Divide_BIsZero_ThrowsDivideByZeroException",
            ],
            passed);
    }

    [Fact]
    public async Task Generate_BooleanReturnAndImpossibleCondition_CoversBothResultsAndListsWhatIsMissing()
    {
        const string source = """
            namespace Sample;
            public class Rules
            {
                public bool IsLarge(int width, int height)
                {
                    return width * height > 10000;
                }

                public int Mode(int code)
                {
                    if (code == 12345 && code % 7 == 3) return 1;
                    return 0;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Rules");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // bool を返す式も判定として扱い、true と false の両方になる入力を探す
        Assert.Contains("[InlineData(10000, 1, false)]", result.Code);
        Assert.Contains("[InlineData(10001, 1, true)]", result.Code);

        // 見つからなかった入力（12345 を 7 で割った余りは 4 で、3 にはならない）は、コメントで書く
        Assert.Contains("// 条件の網羅で、次の入力は見つかりませんでした（必要なら行を手で追加してください）:", result.Code);
        Assert.Contains("//   - code == 12345 && code % 7 == 3 が成立する入力", result.Code);
        Assert.Equal(["IsLarge_BoundaryValues_ReturnsExpected", "Mode_BoundaryValues_ReturnsExpected"], passed);
    }

    [Fact]
    public async Task Generate_StringLiteralDateCombinationAndClamp_CoversEveryCondition()
    {
        const string source = """
            using System;
            public enum Rank { 大吉, 中吉, 小吉, 吉, 凶 }

            public class FortuneTeller
            {
                public int CalcScore(string name, int age, DateTime date)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException("名前が空です", nameof(name));
                    if (age < 0 || age > 150)
                        throw new ArgumentOutOfRangeException(nameof(age));

                    var score = name.Length * 7;

                    if (name.Contains("キティ"))
                        score += 30;

                    if (age >= 60)
                        score += 10;
                    else if (age >= 20)
                        score += 5;
                    else if (age < 6)
                        score += 20;

                    if (date.DayOfWeek == DayOfWeek.Friday && date.Day == 13)
                        score -= 40;
                    else if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                        score += 8;

                    return Math.Clamp(score, 0, 100);
                }
            }
            """;
        var result = TargetCompiler.Generate(source, "FortuneTeller");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // Math.Clamp を計算して Assert.Equal にし、関数の中の文字列（"キティ"）と、13日の金曜日の日付も入力にする
        Assert.Contains("{ \"キティ\", 1, new DateTime(2026, 1, 1), 71 }", result.Code);
        Assert.Contains("{ \"a\", 1, new DateTime(2026, 2, 13), 0 }", result.Code);

        // 区間の中ほどの値（年齢 0〜5 の 3、20〜59 の 40 など）は行にしない
        Assert.DoesNotContain("{ \"a\", 3,", result.Code);
        Assert.DoesNotContain("{ \"a\", 40,", result.Code);
        Assert.DoesNotContain("MatchesSnapshot", result.Code);
        Assert.DoesNotContain("// 条件の網羅で", result.Code);
        Assert.Contains("CalcScore_BoundaryValues_ReturnsExpected", passed);
    }
}
