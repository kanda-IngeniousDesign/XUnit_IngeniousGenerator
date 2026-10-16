using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// 生成したテストコードを、テストプロジェクトのドキュメントとしてソリューションに追加する
/// （リファクタリング・コード修正のどちらからも使う）
/// </summary>
public static class TestDocumentWriter
{
    private const string TestClassSuffix = "Tests";

    /// <summary>
    /// テストプロジェクトに、対象クラスのテストファイル（同じフォルダ構成の XxxTests.cs）が既にあるかを判定する
    /// </summary>
    /// <param name="testProject">テストプロジェクト</param>
    /// <param name="targetDocument">対象クラスがあるドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <returns>既にあればtrue</returns>
    public static bool Exists(Project testProject, Document targetDocument, INamedTypeSymbol targetType)
    {
        var fileName = targetType.Name + TestClassSuffix + ".cs";
        return testProject.Documents.Any(d => d.Name == fileName && d.Folders.SequenceEqual(targetDocument.Folders));
    }

    /// <summary>
    /// テストプロジェクトに、対象クラスのテストファイルを追加したソリューションを返す
    /// （既に同名のファイルがあれば、人間の編集を壊さないよう何もしない）
    /// </summary>
    /// <param name="targetDocument">対象クラスがあるドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">テスト対象のクラスを含むコンパイル</param>
    /// <param name="testProjectId">追加先のテストプロジェクト</param>
    /// <returns>変更後のソリューション</returns>
    public static Solution AddTestDocument(Document targetDocument, INamedTypeSymbol targetType, Compilation compilation, ProjectId testProjectId)
    {
        var solution = targetDocument.Project.Solution;
        var testProject = solution.GetProject(testProjectId);
        if (testProject is null || Exists(testProject, targetDocument, targetType)) return solution;

        // テストプロジェクトが Moq・Verify を参照していれば、モックと実行結果の記録を使う
        var referencedAssemblies = testProject.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath ?? string.Empty));
        var options = GenerationOptions.FromReferencedAssemblies(referencedAssemblies);

        // 対象と同じフォルダ構成に置く
        var folders = targetDocument.Folders;
        var result = TestClassGenerator.Generate(targetType, compilation, BuildTestNamespace(testProject, folders), options);
        var fileName = result.TestClassName + ".cs";

        return solution.AddDocument(
            DocumentId.CreateNewId(testProjectId), fileName, SourceText.From(result.Code), folders, BuildFilePath(testProject, folders, fileName));
    }

    /// <summary>
    /// テストプロジェクトの既定の名前空間にフォルダ階層をつなげ、IDE0130（名前空間とフォルダの一致）に沿った名前空間を作る
    /// </summary>
    /// <param name="testProject">追加先のテストプロジェクト</param>
    /// <param name="folders">プロジェクト内のフォルダ階層</param>
    /// <returns>テストクラスを置く名前空間</returns>
    private static string BuildTestNamespace(Project testProject, IReadOnlyList<string> folders)
    {
        var rootNamespace = testProject.DefaultNamespace ?? testProject.AssemblyName;
        return string.Join(".", new[] { rootNamespace }.Concat(folders).Where(s => 0 < s.Length));
    }

    /// <summary>
    /// テストプロジェクトのフォルダを基準に、追加するファイルのパスを組み立てる
    /// </summary>
    /// <param name="testProject">追加先のテストプロジェクト</param>
    /// <param name="folders">プロジェクト内のフォルダ階層</param>
    /// <param name="fileName">ファイル名</param>
    /// <returns>ファイルパス（プロジェクトのパスが不明ならnull）</returns>
    private static string? BuildFilePath(Project testProject, IReadOnlyList<string> folders, string fileName)
    {
        var projectDir = Path.GetDirectoryName(testProject.FilePath);
        if (string.IsNullOrEmpty(projectDir)) return null;

        return Path.Combine([projectDir!, .. folders, fileName]);
    }
}
