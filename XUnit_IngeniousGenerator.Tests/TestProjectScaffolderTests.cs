using System.Diagnostics;
using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// テストプロジェクトの自動作成を、実際のファイル・dotnet sln add・dotnet test で確かめる
/// </summary>
public sealed class TestProjectScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingenious-xunit-scaffold", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// パッケージの targets を読み込んだプロダクトで、テストプロジェクトを作成し、生成したテストが成功し、
    /// 自動生成できない関数がスキップのテスト（テストしていない一覧）として数えられることを確かめる
    /// </summary>
    [Fact]
    public async Task Create_NewTestProject_AddsToSolutionAndGeneratedTestsPass()
    {
        const string calculator = """
            namespace Product.Domain;

            public class Calculator
            {
                public string Title => "calc".ToUpperInvariant();

                public int Divide(int dividend, int divisor)
                {
                    if (divisor == 0) throw new ArgumentOutOfRangeException(nameof(divisor));
                    return dividend / divisor;
                }

                public string Label(string name) => name.Trim();
            }
            """;
        var productDirectory = Path.Combine(_root, "Product");
        Directory.CreateDirectory(Path.Combine(productDirectory, "Domain"));
        var productProject = Path.Combine(productDirectory, "Product.csproj");
        // パッケージを入れたときと同じく、targets を読み込む（ビルドを壊さないことも確かめる）
        var targets = Path.Combine(AppContext.BaseDirectory, "XUnit_IngeniousGenerator.targets");
        await File.WriteAllTextAsync(productProject, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <Import Project="{targets}" />
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(productDirectory, "Domain", "Calculator.cs"), calculator);
        var solution = Path.Combine(_root, "Sample.slnx");
        await File.WriteAllTextAsync(solution, """
            <Solution>
              <Project Path="Product/Product.csproj" />
            </Solution>
            """);

        // ImplicitUsings を有効にしたプロダクトと同じ前提でコンパイルし、Fix と同じ手順でテストコードを作る
        var compilation = TargetCompiler.Compile("global using System;\n" + calculator);
        var targetType = compilation.GetTypeByMetadataName("Product.Domain.Calculator")!;
        var testProjectDirectory = Path.Combine(_root, "Product.Tests");
        var testCode = TestClassGenerator.Generate(targetType, compilation, "Product.Tests.Domain", new GenerationOptions(useMoq: true, useVerify: true)).Code;

        Assert.Equal(solution, TestProjectScaffolder.FindSolution(productProject));
        var productProjectBefore = await File.ReadAllTextAsync(productProject);
        var result = TestProjectScaffolder.Create(testProjectDirectory, productProject, solution, Path.Combine("Domain", "CalculatorTests.cs"), testCode);

        // アプリ側（プロダクト）の csproj には何も書き足さない（preview.1 の VS では参照が追加されてビルドエラーになった）
        Assert.Equal(productProjectBefore, await File.ReadAllTextAsync(productProject));

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Message);
        var projectFile = await File.ReadAllTextAsync(Path.Combine(testProjectDirectory, "Product.Tests.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", projectFile);
        Assert.Contains(@"<ProjectReference Include=""..\Product\Product.csproj"" />", projectFile);
        Assert.Contains("Product.Tests", await File.ReadAllTextAsync(solution));
        Assert.True(File.Exists(Path.Combine(testProjectDirectory, "Domain", "CalculatorTests.cs")));

        // ソリューション単位の dotnet test は、プロダクトを2か所から同時に復元してまれに衝突するため、先に1回だけ復元する
        var (restoreCode, restoreOutput) = await RunAsync("dotnet", $"restore \"{solution}\" --disable-parallel");
        Assert.True(restoreCode == 0, restoreOutput);
        var (exitCode, output) = await RunAsync("dotnet", $"test \"{solution}\" --nologo --no-restore");
        Assert.True(exitCode == 0, output);
        Assert.Contains("Passed!", output);
        Assert.Matches(@"Skipped:\s+1\b", output);
    }

    /// <summary>
    /// VS Code 向けに、csproj が後から書き出されたら、それを待ってソリューションに追加することを確認する
    /// </summary>
    [Fact]
    public async Task AddToSolutionWhenCreated_ProjectFileAppearsLater_AddsItToSolution()
    {
        Directory.CreateDirectory(_root);
        var solution = Path.Combine(_root, "Sample.slnx");
        await File.WriteAllTextAsync(solution, "<Solution>\n</Solution>\n");
        var projectPath = Path.Combine(_root, "Product.Tests", "Product.Tests.csproj");

        var adding = TestProjectScaffolder.AddToSolutionWhenCreatedAsync(solution, projectPath);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("Product.Tests", await File.ReadAllTextAsync(solution));

        // エディターが Fix を適用して csproj を書き出したのと同じ状態にする
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await adding.WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Contains("Product.Tests/Product.Tests.csproj", await File.ReadAllTextAsync(solution));
    }

    [Fact]
    public async Task AddToSolutionWhenCreated_NoSolution_DoesNothing()
    {
        await TestProjectScaffolder.AddToSolutionWhenCreatedAsync(null, Path.Combine(_root, "X", "X.csproj")).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void FindSolution_DirectoryDoesNotExist_ReturnsNull()
    {
        Assert.Null(TestProjectScaffolder.FindSolution(Path.Combine(_root, "missing", "Product", "Product.csproj")));
    }

    [Fact]
    public void Create_ProjectAlreadyExists_DoesNotOverwrite()
    {
        var testProjectDirectory = Path.Combine(_root, "Product.Tests");
        Directory.CreateDirectory(testProjectDirectory);
        File.WriteAllText(Path.Combine(testProjectDirectory, "Product.Tests.csproj"), "<!-- 既存 -->");

        var result = TestProjectScaffolder.Create(testProjectDirectory, Path.Combine(_root, "Product", "Product.csproj"), null, "CalculatorTests.cs", "// code");

        Assert.False(result.Success);
        Assert.Equal("<!-- 既存 -->", File.ReadAllText(Path.Combine(testProjectDirectory, "Product.Tests.csproj")));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        // 結果（Passed!）を読み取るため、表示言語を英語に固定する
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        // 再利用のために残る MSBuild のプロセスが出力をつかんだままになり、終了を待ち続けないようにする
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
