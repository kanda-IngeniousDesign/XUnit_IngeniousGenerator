using System.Diagnostics;
using System.Globalization;

namespace XUnit_IngeniousGenerator.Cli;

/// <summary>
/// dotnet tool のエントリーポイント
/// </summary>
internal static class Program
{
    private const string ProjectOption = "--project";
    private const string ReportOption = "--report";
    private const string ThresholdOption = "--threshold";

    private const string UsageJa = """
        使い方:
          dotnet ingenious-xunit coverage --project <テストプロジェクトの csproj> [--threshold <行のカバー率の下限(%)>]
          dotnet ingenious-xunit coverage --report <cobertura の XML> [--threshold <行のカバー率の下限(%)>]
        """;

    private const string UsageEn = """
        Usage:
          dotnet ingenious-xunit coverage --project <test project csproj> [--threshold <minimum line coverage (%)>]
          dotnet ingenious-xunit coverage --report <cobertura XML> [--threshold <minimum line coverage (%)>]
        """;

    /// <summary>
    /// コマンドライン引数を解釈して処理を呼び出す
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>終了コード（0: 成功、1: 引数・実行の誤り、2: カバー率が下限未満）</returns>
    private static int Main(string[] args)
    {
        var japanese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
        var options = ParseOptions(args);
        if (args.Length == 0 || args[0] != "coverage" || options is null || (options.Project is null) == (options.Report is null))
        {
            Console.Error.WriteLine(japanese ? UsageJa : UsageEn);
            return 1;
        }

        var report = options.Report ?? RunTests(options.Project!, japanese);
        if (report is null) return 1;

        var coverage = CoverageReport.Parse(File.ReadAllText(report));
        Console.WriteLine(coverage.Format(japanese));
        if (options.Threshold is { } threshold && coverage.LineRate < threshold)
        {
            var rate = coverage.LineRate.ToString("0.0", CultureInfo.InvariantCulture);
            var minimum = threshold.ToString(CultureInfo.InvariantCulture);
            Console.Error.WriteLine(japanese ? $"行のカバー率 {rate}% が下限 {minimum}% を下回っています。" : $"Line coverage {rate}% is below {minimum}%.");
            return 2;
        }

        return 0;
    }

    /// <summary>
    /// オプションを読む
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>オプション（誤りがあればnull）</returns>
    private static CoverageOptions? ParseOptions(string[] args)
    {
        string? project = null;
        string? report = null;
        double? threshold = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args.Length <= i + 1) return null;

            var value = args[++i];
            switch (args[i - 1])
            {
                case ProjectOption:
                    project = value;
                    break;
                case ReportOption:
                    report = value;
                    break;
                case ThresholdOption when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    threshold = parsed;
                    break;
                default:
                    return null;
            }
        }

        return new CoverageOptions(project, report, threshold);
    }

    /// <summary>
    /// テストプロジェクトを、カバレッジを集めながら実行し、結果のファイルを返す
    /// （Microsoft.NET.Test.Sdk に含まれる Microsoft Code Coverage を使うため、パッケージの追加は不要）
    /// </summary>
    /// <param name="project">テストプロジェクトの csproj</param>
    /// <param name="japanese">日本語で書くか</param>
    /// <returns>結果のファイル（テストの実行に失敗した場合はnull）</returns>
    private static string? RunTests(string project, bool japanese)
    {
        var results = Path.Combine(Path.GetTempPath(), "ingenious-xunit-coverage", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "test", project, "--collect", "Code Coverage;Format=cobertura", "--results-directory", results })
        {
            start.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(start)!)
        {
            process.WaitForExit();
        }

        // テストが失敗しても、通ったところのカバレッジは見られるため、結果のファイルがあれば使う
        var file = Directory.Exists(results)
            ? Directory.EnumerateFiles(results, "*.cobertura.xml", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (file is null) Console.Error.WriteLine(japanese ? "カバレッジの結果が作られませんでした。" : "No coverage result was produced.");
        return file;
    }

    /// <summary>
    /// coverage コマンドのオプション
    /// </summary>
    /// <param name="Project">テストプロジェクトの csproj</param>
    /// <param name="Report">作成済みの cobertura の XML</param>
    /// <param name="Threshold">行のカバー率の下限（%）</param>
    private sealed record CoverageOptions(string? Project, string? Report, double? Threshold);
}
