using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 生成した例外テストを実際にビルド・実行し、すべて成功する（＝期待値が正しい）ことまで確かめる
/// </summary>
public class ExceptionTestGenerationTests
{
    [Fact]
    public async Task Generate_IfThrowAndRangeHelpers_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Calculator
            {
                public int Divide(int dividend, int divisor)
                {
                    if (divisor == 0) throw new DivideByZeroException();
                    return dividend / divisor;
                }

                public int Percent(int value)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(value);
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
                    return value;
                }

                public double Scale(double factor)
                {
                    if (0.5 > factor) throw new ArgumentOutOfRangeException(nameof(factor), "too small");
                    return factor;
                }
            }
            """;

        var passed = await GenerateAndRunAsync(source, "Sample.Calculator");

        Assert.Equal(
            [
                "Divide_DivisorIsZero_ThrowsDivideByZeroException",
                "Percent_ValueIsMinus1_ThrowsArgumentOutOfRangeException",
                "Percent_ValueIs101_ThrowsArgumentOutOfRangeException",
                "Scale_FactorIsMinus0Point5_ThrowsArgumentOutOfRangeException",
            ],
            passed);
    }

    [Fact]
    public async Task Generate_StringGuardsAndConstructorCoalesce_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Greeter
            {
                private readonly string _prefix;

                public Greeter(string prefix)
                {
                    _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
                }

                public string Greet(string name, int times)
                {
                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
                    if (times <= 0 || times > 3)
                    {
                        throw new ArgumentOutOfRangeException(nameof(times));
                    }

                    return _prefix + name;
                }
            }
            """;

        var passed = await GenerateAndRunAsync(source, "Sample.Greeter");

        Assert.Equal(
            [
                "Constructor_PrefixIsNull_ThrowsArgumentNullException",
                "Greet_NameIsNull_ThrowsArgumentException",
                "Greet_NameIsEmpty_ThrowsArgumentException",
                "Greet_NameIsWhiteSpace_ThrowsArgumentException",
                "Greet_TimesIsZero_ThrowsArgumentOutOfRangeException",
                "Greet_TimesIs4_ThrowsArgumentOutOfRangeException",
            ],
            passed);
    }

    [Fact]
    public async Task Generate_StaticAsyncHelpers_UsesThrowsAsyncAndStaticCall()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            namespace Sample;
            public static class Store
            {
                public static async Task SaveAsync(string key)
                {
                    ArgumentException.ThrowIfNullOrEmpty(key);
                    await Task.Yield();
                }

                public static ValueTask<int> CountAsync(string? prefix)
                {
                    ArgumentNullException.ThrowIfNull(prefix);
                    return new ValueTask<int>(prefix.Length);
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Store");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("await Assert.ThrowsAsync<ArgumentNullException>(() => Store.SaveAsync(null!))", result.Code);
        Assert.Contains("async () => await Store.CountAsync(null!)", result.Code);
        Assert.DoesNotContain("_sut", result.Code);
        Assert.Equal(
            [
                "SaveAsync_KeyIsNull_ThrowsArgumentNullException",
                "SaveAsync_KeyIsEmpty_ThrowsArgumentException",
                "CountAsync_PrefixIsNull_ThrowsArgumentNullException",
            ],
            OnlyExceptionTests(passed));
    }

    [Fact]
    public async Task Generate_SecondArgumentGuarded_FirstArgumentGetsValidValue()
    {
        const string source = """
            using System;
            namespace Sample;
            public enum Unit { Meter, Feet }
            public class Converter
            {
                public decimal Convert(Unit unit, decimal value, string label, int[] steps)
                {
                    if (label is null || steps == null) throw new ArgumentNullException();
                    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0m);
                    return value;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Converter");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("_sut.Convert(Unit.Meter, 0M, \"a\", Array.Empty<int>())", result.Code);
        Assert.Equal(
            [
                "Convert_LabelIsNull_ThrowsArgumentNullException",
                "Convert_StepsIsNull_ThrowsArgumentNullException",
                "Convert_ValueIsZero_ThrowsArgumentOutOfRangeException",
            ],
            OnlyExceptionTests(passed));
    }

    [Fact]
    public async Task Generate_UnsupportedPatterns_ReportsManualItems()
    {
        const string source = """
            using System;
            namespace Sample;
            public interface IClock { }
            public class Scheduler
            {
                public Scheduler(IClock clock)
                {
                    ArgumentNullException.ThrowIfNull(clock);
                }

                public void Run(int a, int b)
                {
                    if (a > 0 && b > 0) throw new InvalidOperationException();
                    if (b < 0) throw new ArgumentOutOfRangeException(nameof(b));
                }

                public static void Stop(int code)
                {
                    Console.WriteLine(code);
                    if (code < 0) throw new ArgumentOutOfRangeException(nameof(code));
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Scheduler");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Equal(["Constructor_ClockIsNull_ThrowsArgumentNullException"], passed);
        Assert.Equal(
            [
                "Run(int, int): テスト対象のインスタンスを生成できないため対象外です（ガードを通過する引数を決められるpublicコンストラクタがないため。テストプロジェクトに Moq を追加すると、インターフェースの依存を自動でモックします）",
                "Stop(int): ガードより後ろにある throw 1 件は、そこに到達する入力を求められないため例外テストの対象外です",
            ],
            result.ManualItems);
        // 生成できなかった関数・部分は、理由つきのスキップのテストとして一覧に載せる
        Assert.Contains(" / ガードより後ろにある throw 1 件は、そこに到達する入力を求められないため例外テストの対象外です\")]", result.Code);
        Assert.Contains("public void Stop_NotTested()", result.Code);
        Assert.Contains("public void Run_NotTested()", result.Code);
    }

    [Fact]
    public void Generate_UnrecognizedGuardBeforeOthers_StopsAtThatGuard()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Validator
            {
                public void Check(int a, string b)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(a);
                    if (a > 0 && b.Length > 3) throw new InvalidOperationException();
                    ArgumentNullException.ThrowIfNull(b);
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Validator");

        Assert.Equal(2, result.GeneratedTestCount);
        Assert.Contains("Check_AIsMinus1_ThrowsArgumentOutOfRangeException", result.Code);
        Assert.Contains("Check_BoundaryValues_DoesNotThrow", result.Code);
        Assert.Equal(["Check(int, string): 条件 'a > 0 && b.Length > 3' を解析できないため、これ以降のガードは対象外です"], result.ManualItems);
    }

    [Fact]
    public async Task Generate_OverloadsWithSameArity_CastsArguments()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Printer
            {
                public void Print(string text) { ArgumentNullException.ThrowIfNull(text); }
                public void Print(object value) { ArgumentNullException.ThrowIfNull(value); }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Printer");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("_sut.Print((string)null!)", result.Code);
        Assert.Contains("_sut.Print((object)null!)", result.Code);
        // オーバーロードは、テスト名に引数の型を付けて区別する（テストの有無も関数ごとに判定できる）
        Assert.Equal(["Print_String_TextIsNull_ThrowsArgumentNullException", "Print_Object_ValueIsNull_ThrowsArgumentNullException"], OnlyExceptionTests(passed));
    }

    private static async Task<IReadOnlyList<string>> GenerateAndRunAsync(string source, string metadataName)
    {
        var result = TargetCompiler.Generate(source, metadataName);
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);
        Assert.Equal(result.GeneratedTestCount, passed.Count);
        return OnlyExceptionTests(passed);
    }

    // 戻り値テストは ReturnValueGenerationTests で確認するため、ここでは例外テストだけを比べる
    private static List<string> OnlyExceptionTests(IEnumerable<string> testNames)
    {
        return testNames.Where(n => n.Contains("_Throws", StringComparison.Ordinal)).ToList();
    }
}
