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
        var compilation = CSharpCompilation.Create(
            "Target",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "コンパイルエラー:\n" + string.Join("\n", errors) + "\n\n" + string.Join("\n----\n", sources));
        return compilation;
    }

    public static GenerationResult Generate(string targetSource, string metadataName, string? testNamespace = "Sample.Tests")
    {
        var compilation = Compile(targetSource);
        var targetType = compilation.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"{metadataName} が見つかりません。");

        return TestClassGenerator.Generate(targetType, compilation, testNamespace);
    }

    /// <summary>
    /// 対象と生成テストを一緒にビルドし、[Fact] をすべて実行して、成功したテスト名を返す（失敗があれば Assert で落とす）
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunGeneratedTestsAsync(string targetSource, string generatedCode)
    {
        var compilation = Compile(targetSource, generatedCode);
        var warnings = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Warning && d.Location.SourceTree == compilation.SyntaxTrees.Last())
            .ToList();
        Assert.True(warnings.Count == 0, "生成コードの警告:\n" + string.Join("\n", warnings) + "\n\n" + generatedCode);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));

        stream.Position = 0;
        var context = new AssemblyLoadContext(null, isCollectible: true);
        try
        {
            return await RunFactsAsync(context.LoadFromStream(stream), generatedCode);
        }
        finally
        {
            context.Unload();
        }
    }

    private static async Task<IReadOnlyList<string>> RunFactsAsync(Assembly assembly, string generatedCode)
    {
        var passed = new List<string>();
        var failures = new List<string>();
        var facts = assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "FactAttribute"));

        foreach (var fact in facts)
        {
            try
            {
                var instance = Activator.CreateInstance(fact.DeclaringType!);
                if (fact.Invoke(instance, null) is Task task) await task;
                passed.Add(fact.Name);
            }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                failures.Add($"{fact.Name}: {actual.GetType().Name}: {actual.Message}");
            }
        }

        Assert.True(failures.Count == 0, "生成したテストが失敗:\n" + string.Join("\n", failures) + "\n\n" + generatedCode);
        return passed;
    }
}
