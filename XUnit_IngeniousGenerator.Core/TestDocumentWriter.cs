using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 生成したテストコードを、テストプロジェクトのドキュメントとしてソリューションに追加・マージする
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
        return FindTestDocument(testProject, targetDocument, targetType) is not null;
    }

    /// <summary>
    /// テストプロジェクトに対象クラスのテストファイルを追加する。既にあれば、不足しているテストだけをマージする
    /// （既存のテストメソッドは人間が編集している可能性があるため、追加も上書きもしない）
    /// </summary>
    /// <param name="targetDocument">対象クラスがあるドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">テスト対象のクラスを含むコンパイル</param>
    /// <param name="testProjectId">追加先のテストプロジェクト</param>
    /// <param name="onlyMember">この関数・プロパティのテストだけを生成する（null ならクラス全体）</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>変更後のソリューション（追加するものがなければ元のまま）</returns>
    public static async Task<Solution> AddOrMergeTestDocumentAsync(
        Document targetDocument, INamedTypeSymbol targetType, Compilation compilation, ProjectId testProjectId, ISymbol? onlyMember, CancellationToken cancellationToken)
    {
        var solution = targetDocument.Project.Solution;
        var testProject = solution.GetProject(testProjectId);
        if (testProject is null) return solution;

        // テストプロジェクトが Moq・Verify を参照していれば、モックと実行結果の記録を使う
        var referencedAssemblies = testProject.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath ?? string.Empty));
        var options = GenerationOptions.FromReferencedAssemblies(referencedAssemblies).WithOnlyMember(onlyMember);

        // 対象と同じフォルダ構成に置く
        var folders = targetDocument.Folders;
        var result = TestClassGenerator.Generate(targetType, compilation, BuildTestNamespace(testProject, folders), options);
        var fileName = result.TestClassName + ".cs";

        var existing = FindTestDocument(testProject, targetDocument, targetType);
        if (existing is null)
        {
            return solution.AddDocument(
                DocumentId.CreateNewId(testProjectId), fileName, SourceText.From(result.Code), folders, BuildFilePath(testProject, folders, fileName));
        }

        var existingText = await existing.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var merged = TestFileMerger.Merge(existingText.ToString(), result.Code);
        return merged is null ? solution : solution.WithDocumentText(existing.Id, SourceText.From(merged, existingText.Encoding));
    }

    /// <summary>
    /// これから作るテストプロジェクト（Moq・Verify.Xunit を参照する）向けに、テストファイルの内容と置き場所を作る
    /// </summary>
    /// <param name="targetDocument">対象クラスがあるドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">テスト対象のクラスを含むコンパイル</param>
    /// <param name="testProjectName">テストプロジェクト名（既定の名前空間にもなる）</param>
    /// <param name="onlyMember">この関数・プロパティのテストだけを生成する（null ならクラス全体）</param>
    /// <returns>テストプロジェクト内の相対パスと、テストファイルの内容</returns>
    public static (string RelativePath, string Code) GenerateForNewProject(
        Document targetDocument, INamedTypeSymbol targetType, Compilation compilation, string testProjectName, ISymbol? onlyMember = null)
    {
        var folders = targetDocument.Folders;
        var testNamespace = string.Join(".", new[] { testProjectName }.Concat(folders).Where(s => 0 < s.Length));
        var result = TestClassGenerator.Generate(targetType, compilation, testNamespace, new GenerationOptions(useMoq: true, useVerify: true, onlyMember));
        return (Path.Combine([.. folders, result.TestClassName + ".cs"]), result.Code);
    }

    /// <summary>
    /// テストプロジェクトから、対象クラスのテストファイル（同じフォルダ構成の XxxTests.cs）を探す
    /// </summary>
    /// <param name="testProject">テストプロジェクト</param>
    /// <param name="targetDocument">対象クラスがあるドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <returns>テストファイル（なければnull）</returns>
    private static Document? FindTestDocument(Project testProject, Document targetDocument, INamedTypeSymbol targetType)
    {
        var fileName = targetType.Name + TestClassSuffix + ".cs";
        return testProject.Documents.FirstOrDefault(d => d.Name == fileName && d.Folders.SequenceEqual(targetDocument.Folders));
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
