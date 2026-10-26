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

    [Fact]
    public async Task Create_NewTestProject_AddsToSolutionAndGeneratedTestsPass()
    {
        const string calculator = """
            namespace Product.Domain;

            public class Calculator
            {
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
        await File.WriteAllTextAsync(productProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
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
        var result = TestProjectScaffolder.Create(testProjectDirectory, productProject, solution, Path.Combine("Domain", "CalculatorTests.cs"), testCode);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Message);
        var projectFile = await File.ReadAllTextAsync(Path.Combine(testProjectDirectory, "Product.Tests.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", projectFile);
        Assert.Contains(@"<ProjectReference Include=""..\Product\Product.csproj"" />", projectFile);
        Assert.Contains("Product.Tests", await File.ReadAllTextAsync(solution));
        Assert.True(File.Exists(Path.Combine(testProjectDirectory, "Domain", "CalculatorTests.cs")));

        // ソリューション単位の dotnet test は、プロダクトを2か所から同時に復元してまれに衝突するため、先に1回だけ復元する
        var (restoreCode, restoreOutput) = await RunAsync("dotnet", $"restore \"{solution}\"");
        Assert.True(restoreCode == 0, restoreOutput);
        var (exitCode, output) = await RunAsync("dotnet", $"test \"{solution}\" --nologo --no-restore");
        Assert.True(exitCode == 0, output);
        Assert.Contains("Passed!", output);
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
