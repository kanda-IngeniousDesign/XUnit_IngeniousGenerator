using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 入れ子の深いコードを、スタックの小さいスレッド（IDE の解析のプロセスと同程度の 1MB）から解析・生成しても、
/// スタックオーバーフローでプロセスごと落ちないことを確かめる（落ちるとテストの実行全体が中断される）
/// </summary>
public class DeepNestingTests
{
    private const int SmallStack = 1024 * 1024;

    [Fact]
    public void InspectAndGenerate_DeepExpression_DoesNotCrash()
    {
        var expression = string.Join(" + ", Enumerable.Repeat("x", 3000));
        var result = RunOnSmallStack($$"""
            namespace Sample;
            public class Deep
            {
                public int Sum(int x) => {{expression}};
            }
            """);

        Assert.Contains("public void Sum_", result.Code);
    }

    [Fact]
    public void InspectAndGenerate_DeepOrPattern_DoesNotCrash()
    {
        var pattern = string.Join(" or ", Enumerable.Range(0, 1000));
        var result = RunOnSmallStack($$"""
            using System;
            namespace Sample;
            public class Deep
            {
                public int Check(int x)
                {
                    if (x is {{pattern}}) throw new ArgumentException("bad", nameof(x));
                    return x;
                }
            }
            """);

        Assert.Contains("Check_XIsZero_ThrowsArgumentException", result.Code);
    }

    private static GenerationResult RunOnSmallStack(string source)
    {
        var compilation = TargetCompiler.Compile(source);
        var type = compilation.GetTypeByMetadataName("Sample.Deep")!;
        GenerationResult? result = null;
        var thread = new Thread(
            () =>
            {
                TestCoverageInspector.Inspect(type, compilation, new GenerationOptions(useMoq: true, useVerify: true), []);
                result = TestClassGenerator.Generate(type, compilation, "Sample.Tests");
            },
            SmallStack);
        thread.Start();
        thread.Join();
        return result!;
    }
}
