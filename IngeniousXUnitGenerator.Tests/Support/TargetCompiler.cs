using System.Reflection;
using System.Runtime.Loader;
using IngeniousXUnitGenerator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IngeniousXUnitGenerator.Tests.Support;

/// <summary>
/// テスト対象のソースをコンパイルし、生成したテストを実際にビルド・実行して確かめる
/// </summary>
internal static class TargetCompiler
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    // 実行中のテストプロセスが読み込める全アセンブリ（BCL と xUnit を含む）を参照にする
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    public static CSharpCompilation Compile(params string[] sources)
    {
        return Compile(sources.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray());
    }

    private static CSharpCompilation Compile(SyntaxTree[] trees)
    {
        var sources = trees.Select(t => t.ToString()).ToArray();
        var compilation = CSharpCompilation.Create(
            "Target" + Guid.NewGuid().ToString("N"),
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "コンパイルエラー:\n" + string.Join("\n", errors) + "\n\n" + string.Join("\n----\n", sources));
        return compilation;
    }

    public static GenerationResult Generate(
        string targetSource, string metadataName, string? testNamespace = "Sample.Tests", GenerationOptions? options = null)
    {
        var compilation = Compile(targetSource);
        var targetType = compilation.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"{metadataName} が見つかりません。");

        return TestClassGenerator.Generate(targetType, compilation, testNamespace, options);
    }

    /// <summary>
    /// 対象と生成テストを一緒にビルドし、[Fact] をすべて実行して、成功したテスト名を返す（失敗があれば Assert で落とす）
    /// </summary>
    /// <param name="generatedFilePath">生成テストのファイルパス（Verify はこのファイルの隣に記録を保存する）</param>
    public static async Task<IReadOnlyList<string>> RunGeneratedTestsAsync(string targetSource, string generatedCode, string? generatedFilePath = null)
    {
        var compilation = Compile([
            CSharpSyntaxTree.ParseText(targetSource, ParseOptions),
            CSharpSyntaxTree.ParseText(generatedCode, ParseOptions, generatedFilePath ?? string.Empty, System.Text.Encoding.UTF8)]);
        var warnings = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Warning && d.Location.SourceTree == compilation.SyntaxTrees.Last())
            .ToList();
        Assert.True(warnings.Count == 0, "生成コードの警告:\n" + string.Join("\n", warnings) + "\n\n" + generatedCode);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));

        stream.Position = 0;
        // Moq の動的プロキシは解放可能（collectible）なコンテキストを参照できないため、解放しないコンテキストに読み込む
        var context = new AssemblyLoadContext(null, isCollectible: false);
        return await RunFactsAsync(context.LoadFromStream(stream), generatedCode);
    }

    private static async Task<IReadOnlyList<string>> RunFactsAsync(Assembly assembly, string generatedCode)
    {
        var passed = new List<string>();
        var failures = new List<string>();
        var tests = assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes<FactAttribute>().Any());

        foreach (var test in tests)
        {
            // [Theory] は InlineData / MemberData の行ごとに実行する（xUnit と同じく引数の型は完全に一致させる）
            var dataRows = test.GetCustomAttributes<Xunit.Sdk.DataAttribute>().SelectMany(d => d.GetData(test)).ToList();
            var rows = dataRows.Count == 0 ? [null] : dataRows;
            var failed = false;
            foreach (var row in rows)
            {
                try
                {
                    var instance = Activator.CreateInstance(test.DeclaringType!);
                    if (test.Invoke(instance, row) is Task task) await task;
                }
                catch (Exception ex)
                {
                    var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    var arguments = row is null ? string.Empty : $"({string.Join(", ", row)})";
                    failures.Add($"{test.Name}{arguments}: {actual.GetType().Name}: {actual.Message}");
                    failed = true;
                }
            }

            if (!failed) passed.Add(test.Name);
        }

        Assert.True(failures.Count == 0, "生成したテストが失敗:\n" + string.Join("\n", failures) + "\n\n" + generatedCode);
        return passed;
    }
}
