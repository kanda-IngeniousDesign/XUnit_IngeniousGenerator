using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 引数の型の端の値（int.MinValue / int.MaxValue、null など）での挙動を、期待値・例外として記録し、実際に実行して確かめる
/// </summary>
public class TypeExtremeGenerationTests
{
    [Fact]
    public async Task Generate_TypeExtremes_RecordsCurrentBehaviorAndTestsPass()
    {
        const string source = """
            namespace Sample;
            public class Calculator
            {
                public int Twice(int x) => x * 2;
                public int Length(string text) => text.Length;
                public decimal Half(decimal price) => price * 2;
                public double Scale(double value) => value / 2;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Calculator");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // 桁あふれも、今のコードの結果（C# の既定の unchecked）として記録する
        Assert.Contains("[InlineData(int.MaxValue, -2)]", result.Code);
        Assert.Contains("[InlineData(int.MinValue, 0)]", result.Code);
        Assert.Contains("[InlineData(double.MaxValue, 8.988465674311579E+307D)]", result.Code);

        // 実行時の例外は、例外テストとして記録する
        Assert.Contains("Length_TextIsNull_ThrowsNullReferenceException", passed);
        Assert.Contains("Half_PriceIsMaxValue_ThrowsOverflowException", passed);
        Assert.Contains("Assert.Throws<OverflowException>(() => _sut.Half(decimal.MaxValue));", result.Code);
    }

    [Fact]
    public void Generate_CheckedOverflowProject_DoesNotAddIntegerExtremes()
    {
        const string source = """
            namespace Sample;
            public class Calculator
            {
                public int Twice(int x) => x * 2;
            }
            """;

        var compilation = TargetCompiler.Compile(source);
        compilation = compilation.WithOptions(compilation.Options.WithOverflowChecks(true));
        var type = compilation.GetTypeByMetadataName("Sample.Calculator")!;
        var result = XUnit_IngeniousGenerator.Core.TestClassGenerator.Generate(type, compilation, "Sample.Tests");

        Assert.DoesNotContain("int.MaxValue", result.Code);
        Assert.DoesNotContain("int.MinValue", result.Code);
    }

    [Fact]
    public async Task Generate_NullStringHandledByCode_AddsNullRowWithNullableParameter()
    {
        const string source = """
            namespace Sample;
            public class Formatter
            {
                public string Label(string name) => string.IsNullOrEmpty(name) ? "(none)" : name;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Formatter");
        await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("[InlineData(null, \"(none)\")]", result.Code);
        Assert.Contains("public void Label_BoundaryValues_ReturnsExpected(string? name, string expected)", result.Code);
        Assert.Contains("_sut.Label(name!)", result.Code);
    }
}
