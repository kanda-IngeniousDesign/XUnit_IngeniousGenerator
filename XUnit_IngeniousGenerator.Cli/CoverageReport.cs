using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XUnit_IngeniousGenerator.Cli;

/// <summary>
/// カバレッジの結果（cobertura 形式）を、関数ごとの行・分岐のカバー率と、通っていない行にまとめる
/// </summary>
public sealed class CoverageReport
{
    private static readonly Regex ConditionCount = new(@"\((\d+)/(\d+)\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// 結果を作る
    /// </summary>
    /// <param name="methods">関数ごとの結果</param>
    private CoverageReport(IReadOnlyList<MethodCoverage> methods)
    {
        Methods = methods;
    }

    /// <summary>関数ごとの結果</summary>
    public IReadOnlyList<MethodCoverage> Methods { get; }

    /// <summary>全体の行のカバー率（%。行がなければ100）</summary>
    public double LineRate => Rate(Methods.Sum(m => m.LinesCovered), Methods.Sum(m => m.LinesValid));

    /// <summary>
    /// cobertura 形式の XML を読む（テストプロジェクト自身の結果は除く）
    /// </summary>
    /// <param name="xml">XML</param>
    /// <returns>結果</returns>
    public static CoverageReport Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var methods = new List<MethodCoverage>();
        foreach (var package in document.Descendants("package"))
        {
            var packageName = (string?)package.Attribute("name") ?? string.Empty;
            if (packageName.EndsWith(".Tests", StringComparison.Ordinal)) continue;

            foreach (var type in package.Descendants("class"))
            {
                var className = (string?)type.Attribute("name") ?? string.Empty;
                methods.AddRange(type.Descendants("method").Select(m => ParseMethod(className, m)));
            }
        }

        return new CoverageReport(methods);
    }

    /// <summary>
    /// 関数1つの結果を読む
    /// </summary>
    /// <param name="className">クラス名</param>
    /// <param name="method">関数の要素</param>
    /// <returns>結果</returns>
    private static MethodCoverage ParseMethod(string className, XElement method)
    {
        var lines = method.Descendants("line").ToList();
        var uncovered = lines.Where(l => (int)l.Attribute("hits")! == 0).Select(l => (int)l.Attribute("number")!).ToList();
        var branchCovered = 0;
        var branchValid = 0;
        var partial = new List<int>();
        foreach (var line in lines)
        {
            var match = ConditionCount.Match((string?)line.Attribute("condition-coverage") ?? string.Empty);
            if (!match.Success) continue;

            var covered = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var valid = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            branchCovered += covered;
            branchValid += valid;
            if (covered < valid) partial.Add((int)line.Attribute("number")!);
        }

        var name = ((string?)method.Attribute("name") ?? string.Empty) + ((string?)method.Attribute("signature") ?? string.Empty);
        return new MethodCoverage(className, name, lines.Count - uncovered.Count, lines.Count, branchCovered, branchValid, uncovered, partial);
    }

    /// <summary>
    /// 表示用の文字列にする（クラスごとに、関数の行・分岐のカバー率と、通っていない行・分岐が足りない行）
    /// </summary>
    /// <param name="japanese">日本語で書くか</param>
    /// <returns>文字列</returns>
    public string Format(bool japanese)
    {
        var builder = new StringBuilder();
        var linesCovered = Methods.Sum(m => m.LinesCovered);
        var linesValid = Methods.Sum(m => m.LinesValid);
        var branchesCovered = Methods.Sum(m => m.BranchesCovered);
        var branchesValid = Methods.Sum(m => m.BranchesValid);
        builder.Append(japanese ? "合計" : "Total").Append(": ")
            .Append(japanese ? "行" : "Lines").Append(' ').Append(Percent(linesCovered, linesValid)).Append("  ")
            .Append(japanese ? "分岐" : "Branches").Append(' ').Append(Percent(branchesCovered, branchesValid)).AppendLine();

        foreach (var group in Methods.GroupBy(m => m.ClassName))
        {
            builder.AppendLine().AppendLine(group.Key);
            foreach (var method in group) AppendMethod(builder, method, japanese);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 関数1つの結果を書く
    /// </summary>
    /// <param name="builder">書き込み先</param>
    /// <param name="method">関数の結果</param>
    /// <param name="japanese">日本語で書くか</param>
    private static void AppendMethod(StringBuilder builder, MethodCoverage method, bool japanese)
    {
        builder.Append("  ").Append(method.Name).Append("  ")
            .Append(japanese ? "行" : "Lines").Append(' ').Append(Percent(method.LinesCovered, method.LinesValid));
        if (0 < method.BranchesValid)
        {
            builder.Append("  ").Append(japanese ? "分岐" : "Branches").Append(' ').Append(Percent(method.BranchesCovered, method.BranchesValid));
        }

        builder.AppendLine();
        if (0 < method.UncoveredLines.Count)
        {
            builder.Append("    ").Append(japanese ? "通っていない行: " : "Lines not run: ").AppendLine(Ranges(method.UncoveredLines));
        }

        if (0 < method.PartialBranchLines.Count)
        {
            builder.Append("    ").Append(japanese ? "分岐が足りない行: " : "Lines with branches not taken: ").AppendLine(Ranges(method.PartialBranchLines));
        }
    }

    /// <summary>
    /// カバー率を返す（%。対象がなければ100）
    /// </summary>
    /// <param name="covered">通った数</param>
    /// <param name="valid">対象の数</param>
    /// <returns>カバー率</returns>
    private static double Rate(int covered, int valid)
    {
        return valid == 0 ? 100 : covered * 100.0 / valid;
    }

    /// <summary>
    /// カバー率を「80.0% (4/5)」の形にする
    /// </summary>
    /// <param name="covered">通った数</param>
    /// <param name="valid">対象の数</param>
    /// <returns>文字列</returns>
    private static string Percent(int covered, int valid)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Rate(covered, valid):0.0}% ({covered}/{valid})");
    }

    /// <summary>
    /// 行番号を「8, 12-14」の形にまとめる
    /// </summary>
    /// <param name="lines">行番号</param>
    /// <returns>文字列</returns>
    private static string Ranges(IReadOnlyList<int> lines)
    {
        var sorted = lines.Distinct().Order().ToList();
        var parts = new List<string>();
        var start = 0;
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] == sorted[i - 1] + 1) continue;

            parts.Add(start == i - 1
                ? sorted[start].ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{sorted[start]}-{sorted[i - 1]}"));
            start = i;
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// 関数1つのカバレッジ
/// </summary>
/// <param name="ClassName">クラス名</param>
/// <param name="Name">関数名と引数の型</param>
/// <param name="LinesCovered">通った行の数</param>
/// <param name="LinesValid">行の数</param>
/// <param name="BranchesCovered">通った分岐の数</param>
/// <param name="BranchesValid">分岐の数</param>
/// <param name="UncoveredLines">通っていない行</param>
/// <param name="PartialBranchLines">分岐が足りない行</param>
public sealed record MethodCoverage(
    string ClassName,
    string Name,
    int LinesCovered,
    int LinesValid,
    int BranchesCovered,
    int BranchesValid,
    IReadOnlyList<int> UncoveredLines,
    IReadOnlyList<int> PartialBranchLines);
