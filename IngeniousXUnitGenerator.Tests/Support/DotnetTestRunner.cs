using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IngeniousXUnitGenerator.Tests.Support;

/// <summary>
/// 生成したテストを、一時的なテストプロジェクトとして実際の dotnet test で実行する
/// （Verify は実行中の xUnit のテスト情報から記録ファイル名を決めるため、プロセス内では正しく動かない）
/// </summary>
internal static class DotnetTestRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static readonly Regex PassedLine = new(@"^\s+Passed\s+(?<name>[^\s(\[]+)", RegexOptions.Multiline);

    private const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="Moq" Version="4.21.0" />
            <PackageReference Include="Verify.Xunit" Version="31.12.5" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>
    /// 対象のソースと生成したテストを書き出して dotnet test を実行し、成功したテストメソッド名を返す
    /// </summary>
    public static async Task<DotnetTestResult> RunAsync(string directory, string targetSource, string generatedCode, string testFileName)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "Generated.Tests.csproj"), ProjectFile);
        await File.WriteAllTextAsync(Path.Combine(directory, "Target.cs"), targetSource);
        await File.WriteAllTextAsync(Path.Combine(directory, testFileName), generatedCode);

        var startInfo = new ProcessStartInfo("dotnet", ["test", directory, "--nologo", "--logger", "console;verbosity=normal"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet test を起動できません。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(Timeout);
        await process.WaitForExitAsync(cancellation.Token);

        var text = await output + await error;
        var passed = PassedLine.Matches(text).Select(m => m.Groups["name"].Value.Split('.').Last()).Distinct().ToList();
        return new DotnetTestResult(process.ExitCode == 0, passed, text);
    }
}

/// <summary>
/// dotnet test の実行結果
/// </summary>
internal sealed record DotnetTestResult(bool Succeeded, IReadOnlyList<string> PassedTests, string Output);
