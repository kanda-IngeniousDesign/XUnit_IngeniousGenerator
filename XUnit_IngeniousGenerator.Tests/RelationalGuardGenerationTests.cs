using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 引数どうしを比べるガード（min &gt; max など）と、引数のプロパティを使うガード（date.Year &lt; 1900 など）から、
/// 静的な評価で例外になる入力を探してテストを生成し、実際にビルド・実行して成功することまで確かめる
/// </summary>
public class RelationalGuardGenerationTests
{
    [Fact]
    public async Task Generate_ParameterRelationAndPropertyGuards_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Limits
            {
                private readonly int _min;

                public Limits(int min, int max)
                {
                    if (min > max) throw new ArgumentException("min must not exceed max", nameof(min));
                    _min = min;
                }

                public int Clamp(int value, int min, int max)
                {
                    if (min > max) throw new ArgumentException("min must not exceed max", nameof(min));
                    if (value < min || max < value) throw new ArgumentOutOfRangeException(nameof(value));
                    return value - min;
                }

                public int Span(int from, int to)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);
                    ArgumentOutOfRangeException.ThrowIfNegative(from);
                    return to - from;
                }

                public int Age(DateTime birth)
                {
                    if (birth.Year < 1900) throw new ArgumentOutOfRangeException(nameof(birth));
                    return 2026 - birth.Year;
                }

                public int Window(DateTime start, DateTime end)
                {
                    if (end < start) throw new ArgumentException("end is before start", nameof(end));
                    return (end - start).Days;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Limits");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // テスト対象は、コンストラクタのガードを静的な評価で通過する引数で作る
        Assert.Contains("private readonly Limits _sut = new Limits(1, 1);", result.Code);
        Assert.Contains("Assert.Throws<ArgumentException>(() => new Limits(2, 1));", result.Code);
        Assert.Contains("Assert.Equal(\"from\", exception.ParamName);", result.Code);
        Assert.Empty(result.ManualItems);
        Assert.DoesNotContain("_NotTested", result.Code);
        Assert.Equal(
            [
                "Constructor_MinIs2_ThrowsArgumentException",
                "Clamp_ValueIsZero_ThrowsArgumentOutOfRangeException",
                "Clamp_MinIs2_ThrowsArgumentException",
                "Clamp_BoundaryValues_ReturnsExpected",
                "Span_FromIsMinus1_ThrowsArgumentOutOfRangeException",
                "Span_ToIsZero_ThrowsArgumentOutOfRangeException",
                "Span_BoundaryValues_ReturnsExpected",
                "Age_BirthIs18990101_ThrowsArgumentOutOfRangeException",
                "Age_BoundaryValues_ReturnsExpected",
                "Window_StartIs20261231_ThrowsArgumentException",
                "Window_BoundaryValues_ReturnsExpected",
            ],
            passed);
    }

    [Fact]
    public void Generate_RelationGuardWithoutReachableInput_ListsConditionAsNotTested()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Pair
            {
                public int Product(int a, int b)
                {
                    if (a * b == 7919) throw new ArgumentException("unlucky", nameof(a));
                    return a * b;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Pair");

        Assert.Equal(["Product(int, int): 条件 'a * b == 7919' を解析できず、例外になる入力も見つからないため、このガードは対象外です"], result.ManualItems);
    }
}
