using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn_IngeniousCommon;
using XUnit_IngeniousGenerator.Core;

namespace XUnit_IngeniousGenerator;

// プロダクトコードは書き換えない。テストコードの生成はテストプロジェクトへのファイル追加・追記だけで、
// もう一方は「// Ignore TEST001」「// Ignore TEST002」を挿入する抑制専用のFix。
// ・TEST001（クラス名）: クラス全体のテストを生成する（テストプロジェクトがなければ作成する）
// ・TEST001（関数名）  : その関数のテストだけを既存のテストファイルに追加する
// ・TEST002（関数名）  : 自動では生成できないため、テストしていない一覧（スキップのテスト）に載せるか、Ignore を出す
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MissingTestFix)), Shared]
public class MissingTestFix : CodeFixProvider
{
    public sealed override ImmutableArray<string> FixableDiagnosticIds => [MissingTest.DiagnosticId, MissingTest.NotGeneratableDiagnosticId];

    public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics.First();
        MemberDeclarationSyntax? declaration = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent switch
        {
            ClassDeclarationSyntax classDecl => classDecl,
            MethodDeclarationSyntax method => method,
            ConstructorDeclarationSyntax constructor => constructor,
            PropertyDeclarationSyntax property => property,
            IndexerDeclarationSyntax indexer => indexer,
            OperatorDeclarationSyntax op => op,
            ConversionOperatorDeclarationSyntax conversion => conversion,
            _ => null,
        };
        if (declaration is null) return;

        if (diagnostic.Id == MissingTest.NotGeneratableDiagnosticId)
        {
            await RegisterGenerateAsync(context, declaration).ConfigureAwait(false);
            RegisterIgnore(context, declaration, MissingTest.NotGeneratableDiagnosticId, "Ignore : 自動生成できないテストの警告を無視する（別の方法で確認済み）");
            return;
        }

        await RegisterGenerateAsync(context, declaration).ConfigureAwait(false);
        RegisterIgnore(context, declaration, MissingTest.DiagnosticId, "Ignore : テストコードの存在チェックを無視する");
    }

    /// <summary>
    /// テストコードを生成する Fix を、テストプロジェクトごとに登録する（なければテストプロジェクトを作成する Fix）
    /// </summary>
    /// <param name="context">Fix の登録先</param>
    /// <param name="declaration">警告が出ているクラス・関数の宣言</param>
    private static async Task RegisterGenerateAsync(CodeFixContext context, MemberDeclarationSyntax declaration)
    {
        var document = context.Document;
        var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel is null) return;

        // 入れ子のクラスの関数は、一番外側のクラスのテストファイルで扱う
        var classDecl = declaration.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().LastOrDefault();
        if (classDecl is null || semanticModel.GetDeclaredSymbol(classDecl, context.CancellationToken) is not INamedTypeSymbol targetType) return;

        ISymbol? onlyMember = null;
        if (declaration is not ClassDeclarationSyntax)
        {
            onlyMember = MissingTest.GetMemberSymbol(semanticModel, declaration, context.CancellationToken);
            if (onlyMember is null) return;
        }

        var diagnostic = context.Diagnostics.First();
        var testProjects = TestProjectFinder.Find(document.Project.Solution, document.Project);
        if (testProjects.Count == 0)
        {
            var createAction = TestProjectLocator.TryCreateAction(document, targetType, semanticModel.Compilation, onlyMember);
            if (createAction is not null) context.RegisterCodeFix(createAction, diagnostic);
            return;
        }

        foreach (var testProject in testProjects)
        {
            var testProjectId = testProject.Id;
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: GetGenerateTitle(diagnostic.Id, onlyMember, testProject.Name),
                    createChangedSolution: c => TestDocumentWriter.AddOrMergeTestDocumentAsync(
                        document, targetType, semanticModel.Compilation, testProjectId, onlyMember, c),
                    equivalenceKey: $"GenerateTest:{diagnostic.Id}:{testProject.Name}"),
                diagnostic);
        }
    }

    /// <summary>
    /// テストファイルに書き込む Fix のタイトルを返す
    /// </summary>
    /// <param name="diagnosticId">ルールID</param>
    /// <param name="onlyMember">対象の関数・プロパティ（クラス全体なら null）</param>
    /// <param name="testProjectName">テストプロジェクト名</param>
    /// <returns>タイトル</returns>
    private static string GetGenerateTitle(string diagnosticId, ISymbol? onlyMember, string testProjectName)
    {
        if (diagnosticId == MissingTest.NotGeneratableDiagnosticId) return $"Fix : テストしていない一覧（スキップのテスト）に載せる（{testProjectName}）";
        return onlyMember is null ? $"Fix : テストコードを生成する（{testProjectName}）" : $"Fix : この関数のテストコードを追加する（{testProjectName}）";
    }

    /// <summary>
    /// 「// Ignore ルールID」を挿入する Fix を登録する。
    /// ブロック本体を持つメソッド・コンストラクタは、CSharp_IngeniousAnalyzer の関数のルールと同じく本体の1行目に、
    /// それ以外（クラス・式本体のメソッド・プロパティ）は宣言の直前に挿入する
    /// </summary>
    /// <param name="context">Fix の登録先</param>
    /// <param name="declaration">コメントを挿入する宣言</param>
    /// <param name="diagnosticId">ルールID</param>
    /// <param name="title">Fix のタイトル</param>
    private static void RegisterIgnore(CodeFixContext context, MemberDeclarationSyntax declaration, string diagnosticId, string title)
    {
        context.RegisterCodeFix(
            CodeAction.Create(
                title: title,
                createChangedDocument: c => declaration is BaseMethodDeclarationSyntax { Body: not null } method
                    ? context.Document.InsertIgnoreCommentInMethodAsync(method, diagnosticId, c)
                    : context.Document.InsertIgnoreCommentBeforeAsync(declaration, diagnosticId, c),
                equivalenceKey: "Ignore" + diagnosticId),
            context.Diagnostics.First());
    }
}
