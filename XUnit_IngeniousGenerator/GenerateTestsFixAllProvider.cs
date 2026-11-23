using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using XUnit_IngeniousGenerator.Core;

namespace XUnit_IngeniousGenerator;

// ドキュメント・プロジェクト・ソリューション全体の Fix。
// テストの生成は、テストプロジェクトへのファイルの追加と、同じテストファイルへの追記を繰り返すため、
// 標準の BatchFixer（ドキュメント内の書き換えをまとめる）では何も起きなかった。警告を1件ずつ、前の結果のソリューションに順に適用する。
// 「// Ignore」の挿入は、プロダクトコード内の書き換えなので BatchFixer に任せる
internal sealed class GenerateTestsFixAllProvider : FixAllProvider
{
    /// <summary>テストを生成する Fix の EquivalenceKey の先頭（続けて「ルールID:テストプロジェクト名」）</summary>
    public const string KeyPrefix = "GenerateTest:";

    /// <summary>唯一のインスタンス</summary>
    public static GenerateTestsFixAllProvider Instance { get; } = new();

    public override IEnumerable<FixAllScope> GetSupportedFixAllScopes()
    {
        return [FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution];
    }

    public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
    {
        var key = fixAllContext.CodeActionEquivalenceKey;
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return await WellKnownFixAllProviders.BatchFixer.GetFixAsync(fixAllContext).ConfigureAwait(false);
        }

        var testProjectName = key.Substring(key.IndexOf(':', KeyPrefix.Length) + 1);
        var diagnostics = await CollectDiagnosticsAsync(fixAllContext).ConfigureAwait(false);
        if (diagnostics.Count == 0) return null;

        using var language = UiLanguage.Use(MissingTest.LanguageSetting(fixAllContext.Project.AnalyzerOptions));
        var title = diagnostics[0].Diagnostic.Id == MissingTest.NotGeneratableDiagnosticId
            ? Resources.Get("Fix_AddToNotTestedList", testProjectName)
            : Resources.Get("Fix_GenerateTests", testProjectName);
        var setting = MissingTest.LanguageSetting(fixAllContext.Project.AnalyzerOptions);
        return CodeAction.Create(title, c => ApplyAllAsync(fixAllContext.Solution, diagnostics, testProjectName, setting, c), key);
    }

    /// <summary>
    /// 対象範囲（ドキュメント・プロジェクト・ソリューション）の警告を集める
    /// </summary>
    /// <param name="fixAllContext">全体の Fix の情報</param>
    /// <returns>ドキュメントと警告の組</returns>
    private static async Task<List<(DocumentId DocumentId, Diagnostic Diagnostic)>> CollectDiagnosticsAsync(FixAllContext fixAllContext)
    {
        IEnumerable<Document> documents = fixAllContext.Scope switch
        {
            FixAllScope.Document when fixAllContext.Document is not null => [fixAllContext.Document],
            FixAllScope.Project => fixAllContext.Project.Documents,
            _ => fixAllContext.Solution.Projects.SelectMany(p => p.Documents),
        };

        var result = new List<(DocumentId, Diagnostic)>();
        foreach (var document in documents)
        {
            var found = await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
            result.AddRange(found.Select(d => (document.Id, d)));
        }

        return result;
    }

    /// <summary>
    /// 警告ごとにテストを生成し、前の結果のソリューションに順に適用する
    /// </summary>
    /// <param name="solution">元のソリューション</param>
    /// <param name="diagnostics">ドキュメントと警告の組</param>
    /// <param name="testProjectName">テストプロジェクト名</param>
    /// <param name="languageSetting">csproj の言語の設定</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>変更後のソリューション</returns>
    private static async Task<Solution> ApplyAllAsync(
        Solution solution, List<(DocumentId DocumentId, Diagnostic Diagnostic)> diagnostics, string testProjectName, string? languageSetting,
        CancellationToken cancellationToken)
    {
        using var language = UiLanguage.Use(languageSetting);
        foreach (var (documentId, diagnostic) in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = solution.GetDocument(documentId);
            var testProject = solution.Projects.FirstOrDefault(p => p.Name == testProjectName);
            if (document is null || testProject is null) continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var declaration = root is null ? null : MissingTestFix.FindDeclaration(root, diagnostic);
            if (semanticModel is null || declaration is null || MissingTestFix.ResolveTarget(semanticModel, declaration, cancellationToken) is not { } target) continue;

            solution = await TestDocumentWriter.AddOrMergeTestDocumentAsync(
                document, target.TargetType, semanticModel.Compilation, testProject.Id, target.OnlyMember, cancellationToken).ConfigureAwait(false);
        }

        return solution;
    }
}
