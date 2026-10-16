using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// テストプロジェクトがない場合に、xUnit のテストプロジェクトを作ってソリューションに追加する
/// （Roslyn のコード修正の仕組みではプロジェクトを追加できないため、ファイルと dotnet sln add で直接作る）
/// </summary>
public static class TestProjectScaffolder
{
    private const string DefaultTargetFramework = "net8.0";
    private static readonly TimeSpan SolutionAddTimeout = TimeSpan.FromMinutes(1);
    private static readonly Regex TargetFrameworkPattern = new(@"<TargetFrameworks?>\s*([^<;\s]+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// テストプロジェクトを作成し、最初のテストファイルを書き出し、ソリューションに追加する
    /// </summary>
    /// <param name="testProjectDirectory">テストプロジェクトのフォルダ</param>
    /// <param name="productProjectPath">テスト対象のプロジェクト（csproj）のパス</param>
    /// <param name="solutionPath">追加先のソリューション（.sln / .slnx）のパス（見つからなければnull）</param>
    /// <param name="testFileRelativePath">テストファイルのテストプロジェクト内の相対パス</param>
    /// <param name="testCode">テストファイルの内容</param>
    /// <returns>作成結果</returns>
    public static ScaffoldResult Create(
        string testProjectDirectory, string productProjectPath, string? solutionPath, string testFileRelativePath, string testCode)
    {
        var projectName = Path.GetFileName(testProjectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var projectPath = Path.Combine(testProjectDirectory, projectName + ".csproj");
        if (File.Exists(projectPath)) return ScaffoldResult.Failed($"{projectPath} は既にあります");

        Directory.CreateDirectory(testProjectDirectory);
        File.WriteAllText(projectPath, BuildProjectFile(testProjectDirectory, productProjectPath));

        var testFilePath = Path.Combine(testProjectDirectory, testFileRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(testFilePath)!);
        File.WriteAllText(testFilePath, testCode);

        if (solutionPath is null) return ScaffoldResult.Succeeded(projectPath, "ソリューションが見つからないため、ソリューションへの追加は手動で行ってください");
        var addError = AddToSolution(solutionPath, projectPath);
        return addError is null ? ScaffoldResult.Succeeded(projectPath, null) : ScaffoldResult.Succeeded(projectPath, addError);
    }

    /// <summary>
    /// テストプロジェクトのフォルダに、まだプロジェクトファイルがないかを判定する
    /// </summary>
    /// <param name="testProjectDirectory">テストプロジェクトのフォルダ</param>
    /// <returns>作成できるならtrue</returns>
    public static bool CanCreate(string testProjectDirectory)
    {
        return !Directory.Exists(testProjectDirectory) || Directory.GetFiles(testProjectDirectory, "*.csproj").Length == 0;
    }

    /// <summary>
    /// テストプロジェクトの csproj（xUnit・Moq・Verify.Xunit と、テスト対象への参照）を作る
    /// </summary>
    /// <param name="testProjectDirectory">テストプロジェクトのフォルダ</param>
    /// <param name="productProjectPath">テスト対象のプロジェクト（csproj）のパス</param>
    /// <returns>csproj の内容</returns>
    public static string BuildProjectFile(string testProjectDirectory, string productProjectPath)
    {
        var targetFramework = ReadTargetFramework(productProjectPath);
        var reference = GetRelativePath(testProjectDirectory, productProjectPath).Replace('/', '\\');
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <LangVersion>latest</LangVersion>
                <IsPackable>false</IsPackable>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="Moq" Version="4.21.0" />
                <PackageReference Include="Verify.Xunit" Version="31.12.5" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="{reference}" />
              </ItemGroup>

            </Project>

            """;
    }

    /// <summary>
    /// テスト対象のプロジェクトがあるフォルダから上へたどり、最初に見つかったソリューション（.slnx 優先）を返す
    /// </summary>
    /// <param name="productProjectPath">テスト対象のプロジェクト（csproj）のパス</param>
    /// <returns>ソリューションのパス（見つからなければnull）</returns>
    public static string? FindSolution(string productProjectPath)
    {
        for (var directory = Path.GetDirectoryName(productProjectPath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (!Directory.Exists(directory)) continue;

            var found = Directory.GetFiles(directory!, "*.slnx").Concat(Directory.GetFiles(directory!, "*.sln")).FirstOrDefault();
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// テスト対象と同じターゲットフレームワークを使う（複数ある場合は最初のもの）
    /// </summary>
    /// <param name="productProjectPath">テスト対象のプロジェクト（csproj）のパス</param>
    /// <returns>ターゲットフレームワーク</returns>
    private static string ReadTargetFramework(string productProjectPath)
    {
        if (!File.Exists(productProjectPath)) return DefaultTargetFramework;

        var match = TargetFrameworkPattern.Match(File.ReadAllText(productProjectPath));
        return match.Success ? match.Groups[1].Value : DefaultTargetFramework;
    }

    /// <summary>
    /// dotnet sln add でソリューションに追加する（.sln の構成情報も正しく書かれる）
    /// </summary>
    /// <param name="solutionPath">ソリューションのパス</param>
    /// <param name="projectPath">追加するプロジェクトのパス</param>
    /// <returns>失敗した場合のメッセージ（成功ならnull）</returns>
    private static string? AddToSolution(string solutionPath, string projectPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("dotnet", $"sln \"{solutionPath}\" add \"{projectPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return "dotnet を起動できないため、ソリューションへの追加は手動で行ってください";

            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)SolutionAddTimeout.TotalMilliseconds)) return "dotnet sln add が時間内に終わらなかったため、ソリューションへの追加を確認してください";
            return process.ExitCode == 0 ? null : $"ソリューションへの追加に失敗しました: {error.Trim()}";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // dotnet が PATH にない環境
            return "dotnet が見つからないため、ソリューションへの追加は手動で行ってください";
        }
    }

    /// <summary>
    /// フォルダから見たファイルの相対パスを求める（netstandard2.0 には Path.GetRelativePath がないため Uri で計算する）
    /// </summary>
    /// <param name="fromDirectory">基準のフォルダ</param>
    /// <param name="toPath">対象のファイル</param>
    /// <returns>相対パス</returns>
    private static string GetRelativePath(string fromDirectory, string toPath)
    {
        var from = new Uri(Path.GetFullPath(fromDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var to = new Uri(Path.GetFullPath(toPath));
        return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
    }
}

/// <summary>
/// テストプロジェクトの作成結果
/// </summary>
public sealed class ScaffoldResult
{
    private ScaffoldResult(bool success, string? projectPath, string? message)
    {
        Success = success;
        ProjectPath = projectPath;
        Message = message;
    }

    /// <summary>作成できたか</summary>
    public bool Success { get; }

    /// <summary>作成したプロジェクトのパス</summary>
    public string? ProjectPath { get; }

    /// <summary>補足（ソリューションへの追加に失敗した場合など）</summary>
    public string? Message { get; }

    /// <summary>
    /// 作成できた結果を作る
    /// </summary>
    /// <param name="projectPath">作成したプロジェクトのパス</param>
    /// <param name="message">補足</param>
    /// <returns>作成結果</returns>
    public static ScaffoldResult Succeeded(string projectPath, string? message)
    {
        return new ScaffoldResult(true, projectPath, message);
    }

    /// <summary>
    /// 作成できなかった結果を作る
    /// </summary>
    /// <param name="message">理由</param>
    /// <returns>作成結果</returns>
    public static ScaffoldResult Failed(string message)
    {
        return new ScaffoldResult(false, null, message);
    }
}
