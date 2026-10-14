using System.Composition;
using IngeniousXUnitGenerator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace IngeniousXUnitGenerator.Analyzers;

/// <summary>
/// クラス名の上で「このクラスのテストコードを生成」を提供する
/// </summary>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(GenerateTestClassRefactoring)), Shared]
public class GenerateTestClassRefactoring : CodeRefactoringProvider
{
    /// <summary>
    /// カーソル位置がクラス名の上なら、テストプロジェクトごとに生成アクションを登録する
    /// </summary>
    /// <param name="context">リファクタリングのコンテキスト</param>
    /// <returns>非同期処理</returns>
    public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var document = context.Document;
        var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        // クラス本体のどこでも出すとメニューがうるさいため、クラス名の上に限定する
        var classDecl = root.FindToken(context.Span.Start).Parent as ClassDeclarationSyntax;
        if (classDecl is null || !classDecl.Identifier.Span.IntersectsWith(context.Span)) return;

        var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel?.GetDeclaredSymbol(classDecl, context.CancellationToken) is not { } targetType) return;

        foreach (var testProject in TestProjectFinder.Find(document.Project.Solution, document.Project))
        {
            var testProjectId = testProject.Id;
            context.RegisterRefactoring(
                CodeAction.Create(
                    title: $"このクラスのテストコードを生成（{testProject.Name}）",
                    createChangedSolution: c => AddTestDocumentAsync(document, targetType, testProjectId, c),
                    equivalenceKey: $"GenerateTestClass:{testProject.Name}"));
        }
    }

    /// <summary>
    /// テストプロジェクトに、対象クラスのテストファイルを追加したソリューションを返す
    /// </summary>
    /// <param name="document">対象クラスがあるドキュメント</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="testProjectId">追加先のテストプロジェクト</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>変更後のソリューション</returns>
    private static Task<Solution> AddTestDocumentAsync(
        Document document, INamedTypeSymbol targetType, ProjectId testProjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var solution = document.Project.Solution;
        var testProject = solution.GetProject(testProjectId);
        if (testProject is null) return Task.FromResult(solution);

        // 対象と同じフォルダ構成に置き、既に同名ファイルがあれば人間の編集を壊さないよう何もしない（マージは後続で実装）
        var folders = document.Folders;
        var result = TestClassGenerator.Generate(targetType, BuildTestNamespace(testProject, folders));
        var fileName = result.TestClassName + ".cs";
        if (testProject.Documents.Any(d => d.Name == fileName && d.Folders.SequenceEqual(folders))) return Task.FromResult(solution);

        var filePath = BuildFilePath(testProject, folders, fileName);
        var newSolution = solution.AddDocument(
            DocumentId.CreateNewId(testProjectId), fileName, SourceText.From(result.Code), folders, filePath);
        return Task.FromResult(newSolution);
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
        return string.Join(".", new[] { rootNamespace }.Concat(folders).Where(s => s.Length > 0));
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
