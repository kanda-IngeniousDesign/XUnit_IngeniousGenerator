using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// internal の型を引数に持つ関数でも、public のテストメソッドとしてビルドできる形で生成することを確かめる
/// （public のテストメソッドの引数に internal の型は書けない（CS0051）ため、object で受け取って元の型にキャストする）
/// </summary>
public class InternalTypeGenerationTests
{
    private const string Source = """
        namespace Sample;
        internal enum Rank { Low, High }
        class Pricing
        {
            public void Apply(int amount, Rank rank) { }
            public int Bonus(int amount, Rank rank) => rank == Rank.High ? amount * 2 : amount;
        }
        """;

    [Fact]
    public async Task Generate_InternalEnumParameter_ReceivesAsObjectAndCasts()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Pricing");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, result.Code);

        Assert.Contains("Apply_BoundaryValues_DoesNotThrow", passed);
        Assert.Contains("(int amount, object rank)", result.Code);
        Assert.Contains("_sut.Apply(amount, (Rank)rank)", result.Code);
    }

    [Fact]
    public void Generate_InternalEnumParameterWithSnapshot_Compiles()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Pricing", options: new GenerationOptions(useMoq: true, useVerify: true));

        // 実行は Verify の記録ファイルが必要なため、ビルドが通ることだけを確かめる（エラーがあれば Compile が失敗する）
        TargetCompiler.Compile(Source, result.Code);
        Assert.Contains("_sut.Bonus(amount, (Rank)rank)", result.Code);
    }
}
