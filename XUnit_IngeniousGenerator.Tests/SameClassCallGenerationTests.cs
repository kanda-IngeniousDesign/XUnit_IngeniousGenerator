using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// public の関数から呼んでいる同じクラスの関数（private など）の中身もたどって、期待値・境界値・例外テストを作れることを確かめる
/// </summary>
public class SameClassCallGenerationTests
{
    [Fact]
    public async Task Generate_PrivateHelper_ComputesExpectedValuesAndUsesHelperBoundaries()
    {
        const string source = """
            namespace Sample;
            public class Pricing
            {
                public int Price(int quantity) => Discount(quantity) + Base();
                private int Discount(int count) => count >= 100 ? count * 8 : count * 10;
                private static int Base() => 50;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Pricing");
        await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // private の関数の比較（count >= 100）から境界値を作り、戻り値も計算する
        Assert.Contains("Price_BoundaryValues_ReturnsExpected", result.Code);
        Assert.Contains("[InlineData(99, 1040)]", result.Code);
        Assert.Contains("[InlineData(100, 850)]", result.Code);
        Assert.DoesNotContain("MatchesSnapshot", result.Code);
    }

    [Fact]
    public async Task Generate_PrivateValidator_CreatesExceptionTest()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Account
            {
                public int Withdraw(int amount)
                {
                    Validate(amount);
                    return 1000 - amount;
                }

                private static void Validate(int value)
                {
                    if (value > 1000) throw new InvalidOperationException("残高不足");
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Account");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains(passed, n => n.Contains("Withdraw_", StringComparison.Ordinal) && n.Contains("ThrowsInvalidOperationException", StringComparison.Ordinal));
        Assert.Contains("Withdraw_BoundaryValues_ReturnsExpected", result.Code);
    }

    [Fact]
    public void Generate_RecursiveHelper_IsNotComputed()
    {
        const string source = """
            namespace Sample;
            public class MathUtil
            {
                public int Factorial(int n) => Calc(n);
                private int Calc(int n) => n <= 1 ? 1 : n * Calc(n - 1);
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.MathUtil");

        Assert.DoesNotContain("Factorial_BoundaryValues_ReturnsExpected", result.Code);
        Assert.Contains(result.ManualItems, i => i.Contains("再帰呼び出し", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_PrivateHelperWithFileAccess_IsNotGeneratable()
    {
        const string source = """
            using System.IO;
            namespace Sample;
            public class Reader
            {
                public int Count(string path) => Load(path).Length;
                private static string Load(string path) => File.ReadAllText(path);
            }
            """;

        var compilation = TargetCompiler.Compile(source);
        var type = compilation.GetTypeByMetadataName("Sample.Reader")!;
        var coverages = TestCoverageInspector.Inspect(type, compilation, new GenerationOptions(useMoq: true, useVerify: true), []);

        var count = Assert.Single(coverages, c => c.Member.Name == "Count");
        Assert.Equal(CoverageStatus.NotGeneratable, count.Status);
        Assert.Contains(count.Reasons, r => r.Contains("ファイル", StringComparison.Ordinal));
    }
}
