using IngeniousXUnitGenerator.Core;
using IngeniousXUnitGenerator.Tests.Support;

namespace IngeniousXUnitGenerator.Tests;

/// <summary>
/// Verify による実行結果の記録と、Moq による依存の自動モックを確かめる（生成したテストを実際の dotnet test で実行する）
/// </summary>
public sealed class SnapshotAndMockGenerationTests : IDisposable
{
    private static readonly GenerationOptions MoqAndVerify = new(useMoq: true, useVerify: true);

    // Verify は生成テストのファイルの隣に記録（.verified.txt）を保存するため、テストごとに作業フォルダを分ける
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ingenious-xunit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact]
    public async Task Generate_NotStaticallyComputable_RecordsOnFirstRunAndDetectsChangesLater()
    {
        const string source = """
            namespace Sample;
            public class Formatter
            {
                public string Format(string label, int digits)
                {
                    var text = label.Trim();
                    return digits < 0 ? text : text.PadLeft(digits, '0');
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Formatter", options: MoqAndVerify);

        Assert.Contains("public Task Format_BoundaryValues_MatchesSnapshot(string label, int digits)", result.Code);
        Assert.Contains(
            "return Verifier.Verify(_sut.Format(label, digits)).UseParameters(label, digits).AutoVerify(verifiedFile => !File.Exists(verifiedFile), includeBuildServer: false, throwException: false);",
            result.Code);
        Assert.Empty(result.ManualItems);

        // 1回目: 記録がないので自動で記録される
        var first = await DotnetTestRunner.RunAsync(_workDirectory, source, result.Code, "FormatterTests.cs");
        Assert.True(first.Succeeded, first.Output);
        var verified = Directory.GetFiles(_workDirectory, "*.verified.txt");
        Assert.Contains(verified, f => Path.GetFileName(f) == "FormatterTests.Format_BoundaryValues_MatchesSnapshot_label=a_digits=1.verified.txt");

        // 2回目: 記録と一致するので成功する
        var second = await DotnetTestRunner.RunAsync(_workDirectory, source, result.Code, "FormatterTests.cs");
        Assert.True(second.Succeeded, second.Output);

        // 対象の挙動が変わると、記録との違いでテストが失敗する
        var changed = source.Replace("label.Trim()", "label.Trim().ToUpperInvariant()", StringComparison.Ordinal);
        var third = await DotnetTestRunner.RunAsync(_workDirectory, changed, result.Code, "FormatterTests.cs");
        Assert.False(third.Succeeded, third.Output);
        Assert.Contains("VerifyException", third.Output);
    }

    [Fact]
    public async Task Generate_InterfaceDependency_MocksConstructorArgumentAndUnblocksTests()
    {
        const string source = """
            using System;
            namespace Sample;
            public interface IClock { int Hour(); }
            public class Greeter
            {
                private readonly IClock _clock;

                public Greeter(IClock clock)
                {
                    ArgumentNullException.ThrowIfNull(clock);
                    _clock = clock;
                }

                public string Greet(string name)
                {
                    ArgumentException.ThrowIfNullOrEmpty(name);
                    return (_clock.Hour() < 12 ? "Good morning, " : "Hello, ") + name;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Greeter", options: MoqAndVerify);
        var run = await DotnetTestRunner.RunAsync(_workDirectory, source, result.Code, "GreeterTests.cs");

        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("private readonly Greeter _sut = new Greeter(new Mock<IClock>().Object);", result.Code);
        Assert.Contains("using Moq;", result.Code);
        Assert.Empty(result.ManualItems);
        Assert.Equal(
            [
                "Constructor_ClockIsNull_ThrowsArgumentNullException",
                "Greet_BoundaryValues_MatchesSnapshot",
                "Greet_NameIsEmpty_ThrowsArgumentException",
                "Greet_NameIsNull_ThrowsArgumentNullException",
            ],
            run.PassedTests.Order());
    }

    [Fact]
    public async Task Generate_InterfaceParameterAndTaskResult_UsesFixedMockAndVerifiesTask()
    {
        const string source = """
            using System.Threading.Tasks;
            namespace Sample;
            public interface IRepository { string Find(int id); }
            public class Service
            {
                public string Describe(IRepository repository) => "item:" + repository.Find(1);

                public async Task<string> LoadAsync(string key)
                {
                    await Task.Yield();
                    return key.ToUpperInvariant();
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Service", options: MoqAndVerify);
        var run = await DotnetTestRunner.RunAsync(_workDirectory, source, result.Code, "ServiceTests.cs");

        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("Verifier.Verify(_sut.Describe(new Mock<IRepository>().Object))", result.Code);
        Assert.Contains("Verifier.Verify(_sut.LoadAsync(key)).UseParameters(key)", result.Code);
        Assert.Equal(["Describe_DefaultInputs_MatchesSnapshot", "LoadAsync_BoundaryValues_MatchesSnapshot"], run.PassedTests.Order());
    }

    [Fact]
    public async Task Generate_ThrowLaterInBody_LeavesThrowingValuesOutOfSnapshotInputs()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Formatter
            {
                public string Format(string label, int digits)
                {
                    var text = label.Trim();
                    if (digits < 0) throw new ArgumentOutOfRangeException(nameof(digits));
                    return text.PadLeft(digits, '0');
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Formatter", options: MoqAndVerify);
        var run = await DotnetTestRunner.RunAsync(_workDirectory, source, result.Code, "FormatterTests.cs");

        Assert.True(run.Succeeded, run.Output);
        Assert.DoesNotContain("[InlineData(\"a\", -1)]", result.Code);
        Assert.Contains("[InlineData(\"a\", 0)]", result.Code);
    }

    [Fact]
    public void Generate_TimeAndFileDependencies_ReportsNotGeneratable()
    {
        const string source = """
            using System;
            using System.IO;
            namespace Sample;
            public class Report
            {
                public string Stamp(string title) => title + DateTime.Now.ToString("yyyy");

                public string Load(string path) => File.ReadAllText(path);
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Report", options: MoqAndVerify);

        Assert.Equal(0, result.GeneratedTestCount);
        Assert.Empty(result.ManualItems);
        Assert.Equal(
            [
                "Stamp(string): 'Now' は実行するたびに結果が変わるため、単体テストでは担保しません（UIオーケストレーター側で確認）",
                "Load(string): 'File' は外部環境（ファイル・DB・通信など）に依存するため、単体テストでは担保しません（UIオーケストレーター側で確認）",
            ],
            result.NotGeneratableItems);
        Assert.Contains("    // 生成不可: Stamp(string):", result.Code);
    }

    [Fact]
    public void Generate_WithoutMoqAndVerify_SuggestsAddingPackages()
    {
        const string source = """
            namespace Sample;
            public interface IClock { int Hour(); }
            public class Formatter
            {
                public string Format(string label) => label.Trim();

                public int Hours(IClock clock) => clock.Hour();
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Formatter");

        Assert.Equal(0, result.GeneratedTestCount);
        Assert.Equal(2, result.ManualItems.Count);
        Assert.EndsWith("テストプロジェクトに Verify.Xunit を追加すると、実行結果を記録するテストを生成します", result.ManualItems[0]);
        Assert.EndsWith("テストプロジェクトに Moq を追加すると、インターフェースの依存を自動でモックします", result.ManualItems[1]);
    }

    [Fact]
    public void FromReferencedAssemblies_DetectsMoqAndVerify()
    {
        var options = GenerationOptions.FromReferencedAssemblies(["xunit.core", "moq", "Verify.Xunit"]);

        Assert.True(options.UseMoq);
        Assert.True(options.UseVerify);
        Assert.False(GenerationOptions.FromReferencedAssemblies(["xunit.core"]).UseMoq);
    }
}
