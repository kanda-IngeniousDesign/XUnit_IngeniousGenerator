using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// ref / out / in の引数を持つ関数のテストを生成し、実際にビルド・実行して成功することまで確かめる
/// （out は out _ で受け、ref はテストの引数か配列の要素を渡し、in は値をそのまま渡す）
/// </summary>
public class RefParameterGenerationTests
{
    [Fact]
    public async Task Generate_RefOutInParameters_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Parser
            {
                public bool TryHalf(int value, out int half)
                {
                    if (value % 2 != 0)
                    {
                        half = 0;
                        return false;
                    }

                    half = value / 2;
                    return true;
                }

                public int AddTo(ref int total, int amount)
                {
                    if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
                    total += amount;
                    return total;
                }

                public int Twice(in int value) => value * 2;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Parser");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("public void TryHalf_BoundaryValues_ReturnsExpected(int value, bool expected)", result.Code);
        Assert.Contains("_sut.TryHalf(value, out _)", result.Code);
        Assert.Contains("_sut.AddTo(ref total, amount)", result.Code);
        Assert.Contains("_sut.AddTo(ref new int[] { 0 }[0], -1)", result.Code);
        Assert.Contains("_sut.Twice(value)", result.Code);
        Assert.DoesNotContain("NotTested", result.Code);
        Assert.Equal(
            [
                "TryHalf_BoundaryValues_ReturnsExpected",
                "AddTo_AmountIsMinus1_ThrowsArgumentOutOfRangeException",
                "AddTo_BoundaryValues_ReturnsExpected",
                "Twice_BoundaryValues_ReturnsExpected",
            ],
            passed);
    }
}
