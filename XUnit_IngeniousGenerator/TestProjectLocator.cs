using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using XUnit_IngeniousGenerator.Core;

namespace XUnit_IngeniousGenerator;

// テストプロジェクトを作る場所を決める（targets が渡す IngeniousTestProjectDirectory を優先し、なければ隣の「プロジェクト名.Tests」）
internal static class TestProjectLocator
{
    /// <summary>
    /// テストプロジェクトを作成してテストコードを生成するアクションを作る（作れない場合は null）
    /// </summary>
    /// <param name="document">対象のドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">コンパイル</param>
    /// <param name="onlyMember">この関数だけを生成する場合の関数</param>
    /// <returns>アクション（作れない場合はnull）</returns>
    public static CodeAction? TryCreateAction(
        Document document, INamedTypeSymbol targetType, Compilation compilation, IMethodSymbol? onlyMember = null)
    {
        var directory = GetTestProjectDirectory(document.Project);
        if (directory is null || !TestProjectScaffolder.CanCreate(directory)) return null;

        return new CreateTestProjectCodeAction(document, targetType, compilation, directory, onlyMember);
    }

    /// <summary>
    /// テストプロジェクトのフォルダを求める
    /// </summary>
    /// <param name="project">プロダクトのプロジェクト</param>
    /// <returns>テストプロジェクトのフォルダ（求められなければnull）</returns>
    private static string? GetTestProjectDirectory(Project project)
    {
        var options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
        if (options.TryGetValue(MissingTest.TestProjectDirectoryKey, out var configured) && !string.IsNullOrWhiteSpace(configured)) return configured;

        var projectDirectory = Path.GetDirectoryName(project.FilePath);
        if (string.IsNullOrEmpty(projectDirectory)) return null;

        var parent = Path.GetDirectoryName(projectDirectory) ?? projectDirectory;
        return Path.Combine(parent, project.Name + ".Tests");
    }
}
