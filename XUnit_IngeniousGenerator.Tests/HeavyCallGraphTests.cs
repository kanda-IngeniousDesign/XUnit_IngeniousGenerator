using System.Diagnostics;
using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 同じクラスの関数を何度も呼び合うクラスでも、解析が短時間で終わることを確かめる
/// （IDE では入力のたびに解析が走るため、処理量が爆発すると解析のプロセスが落ちる）
/// </summary>
public class HeavyCallGraphTests
{
    [Fact]
    public void Inspect_ManyCallsBetweenHelpers_FinishesQuickly()
    {
        // Step0 → Step1 を6回、Step1 → Step2 を6回 … と呼ぶ（たどり直すと 6^深さ 回になる）
        var steps = string.Join("\n", Enumerable.Range(0, 12).Select(i => i == 11
            ? $"    private int Step{i}(int x) => x > {i} ? x : -x;"
            : $"    private int Step{i}(int x) => Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x) + Step{i + 1}(x);"));
        var source = $$"""
            namespace Sample;
            public class Pipeline
            {
                public int Run(int x) => Step0(x);
            {{steps}}
            }
            """;

        var compilation = TargetCompiler.Compile(source);
        var type = compilation.GetTypeByMetadataName("Sample.Pipeline")!;
        var stopwatch = Stopwatch.StartNew();
        TestCoverageInspector.Inspect(type, compilation, new GenerationOptions(useMoq: true, useVerify: true), []);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"解析に {stopwatch.Elapsed.TotalSeconds:F1} 秒かかりました");
    }

    [Fact]
    public void Inspect_ManyCallsWithinDepthLimit_FinishesQuickly()
    {
        // 深さの上限より浅い呼び出しでも、1回の評価で 10^6 回の呼び出しになる
        var steps = string.Join("\n", Enumerable.Range(0, 7).Select(i => i == 6
            ? $"    private int Step{i}(int x) => x > {i} ? 1 : 0;"
            : $"    private int Step{i}(int x) => " + string.Join(" + ", Enumerable.Repeat($"Step{i + 1}(x)", 10)) + ";"));
        var source = $$"""
            namespace Sample;
            public class Pipeline
            {
                public int Run(int x) => Step0(x);
            {{steps}}
            }
            """;

        var compilation = TargetCompiler.Compile(source);
        var type = compilation.GetTypeByMetadataName("Sample.Pipeline")!;
        var stopwatch = Stopwatch.StartNew();
        TestCoverageInspector.Inspect(type, compilation, new GenerationOptions(useMoq: true, useVerify: true), []);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"解析に {stopwatch.Elapsed.TotalSeconds:F1} 秒かかりました");
    }
}
