using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// internal のクラスをテストプロジェクトから使えるよう、プロダクトのプロジェクトに InternalsVisibleTo を追加する
/// （csproj は編集せず、属性を書いたファイルを1つ追加する）
/// </summary>
public static class InternalsVisibleToWriter
{
    private const string AttributeName = "System.Runtime.CompilerServices.InternalsVisibleToAttribute";
    private const string FileName = "InternalsVisibleTo";

    /// <summary>
    /// テスト対象のクラスが public でなく、テストプロジェクトへの InternalsVisibleTo もまだないかを判定する
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">プロダクトのコンパイル</param>
    /// <param name="testAssemblyName">テストプロジェクトのアセンブリ名</param>
    /// <returns>追加が必要ならtrue</returns>
    public static bool IsNeeded(INamedTypeSymbol targetType, Compilation compilation, string testAssemblyName)
    {
        if (targetType.DeclaredAccessibility == Accessibility.Public) return false;

        // csproj の <InternalsVisibleTo Include="..." /> も、ビルド時に生成される属性としてコンパイルに含まれる
        return !compilation.Assembly.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == AttributeName
            && a.ConstructorArguments is { Length: 1 } arguments
            && arguments[0].Value is string granted
            && string.Equals(granted.Split(',')[0].Trim(), testAssemblyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 必要なら、プロダクトのプロジェクトに InternalsVisibleTo を書いたファイルを追加する
    /// </summary>
    /// <param name="solution">ソリューション</param>
    /// <param name="productProjectId">プロダクトのプロジェクト</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">プロダクトのコンパイル</param>
    /// <param name="testAssemblyName">テストプロジェクトのアセンブリ名</param>
    /// <returns>変更後のソリューション（不要なら元のまま）</returns>
    public static Solution AddIfNeeded(
        Solution solution, ProjectId productProjectId, INamedTypeSymbol targetType, Compilation compilation, string testAssemblyName)
    {
        var project = solution.GetProject(productProjectId);
        if (project?.FilePath is null || !IsNeeded(targetType, compilation, testAssemblyName)) return solution;

        // 同じ名前のファイルがあれば、テストプロジェクト名を付けて別のファイルにする
        var directory = Path.GetDirectoryName(project.FilePath)!;
        var fileName = FileName + ".cs";
        if (project.Documents.Any(d => string.Equals(d.FilePath, Path.Combine(directory, fileName), StringComparison.OrdinalIgnoreCase)))
        {
            fileName = $"{FileName}.{testAssemblyName}.cs";
        }

        return solution.AddDocument(
            DocumentId.CreateNewId(productProjectId), fileName, SourceText.From(BuildCode(testAssemblyName)), filePath: Path.Combine(directory, fileName));
    }

    /// <summary>
    /// InternalsVisibleTo を書いたファイルの内容を作る
    /// </summary>
    /// <param name="testAssemblyName">テストプロジェクトのアセンブリ名</param>
    /// <returns>ファイルの内容</returns>
    public static string BuildCode(string testAssemblyName)
    {
        return "using System.Runtime.CompilerServices;\n\n"
            + $"// テストプロジェクト（{testAssemblyName}）から internal のクラスをテストできるようにする\n"
            + $"[assembly: InternalsVisibleTo(\"{testAssemblyName}\")]\n";
    }
}
