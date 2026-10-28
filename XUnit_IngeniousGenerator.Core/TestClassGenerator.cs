using XUnit_IngeniousGenerator.Core.Generation;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 対象クラスからxUnitのテストクラスを生成する
/// </summary>
public static class TestClassGenerator
{
    private const string TestClassSuffix = "Tests";
    private const string XUnitNamespace = "Xunit";

    /// <summary>
    /// 対象クラスに対応するテストファイル1つ分のコードを生成する
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">テスト対象のクラスを含むコンパイル</param>
    /// <param name="testNamespace">テストクラスを置く名前空間（nullや空ならグローバル名前空間）</param>
    /// <param name="options">テストプロジェクトで使えるパッケージに応じた設定（省略時は Moq・Verify を使わない）</param>
    /// <returns>生成結果</returns>
    public static GenerationResult Generate(INamedTypeSymbol targetType, Compilation compilation, string? testNamespace, GenerationOptions? options = null)
    {
        var testClassName = targetType.Name + TestClassSuffix;
        var renderer = new ValueRenderer();
        var context = new GenerationContext(compilation, options ?? GenerationOptions.None);
        var tests = PlanTests(targetType, context, out var sut);

        // 文字列連結ではなくSyntaxFactoryで組み立て、整形はRoslynに任せる
        var builder = new TestSyntaxBuilder(targetType, renderer);
        var members = new List<MemberDeclarationSyntax>(tests.Count + 1);
        if (tests.Any(t => t.UsesSut)) members.Add(builder.BuildSutField(sut));
        members.AddRange(tests.SelectMany(t => t.Build(builder)));

        var testClass = ClassDeclaration(testClassName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddMembers([.. members]);

        renderer.AddNamespace(XUnitNamespace);
        if (targetType.ContainingNamespace is { IsGlobalNamespace: false } targetNamespace) renderer.AddNamespace(targetNamespace.ToDisplayString());

        var compilationUnit = CompilationUnit()
            .AddUsings(BuildUsings(renderer.Namespaces, testNamespace))
            .AddMembers(WrapInNamespace(testNamespace, testClass))
            .NormalizeWhitespace(eol: "\r\n");

        var formatted = Format(compilationUnit);
        var generatedCount = tests.Count(t => t is not NotTestedTestCase);
        return new GenerationResult(testClassName, formatted.ToFullString(), generatedCount, context.ManualItems, context.NotGeneratableItems);
    }

    /// <summary>
    /// クラス（入れ子のクラスを含む）の関数ごとにテストを計画する。
    /// 生成できない関数・生成できなかった部分は、理由つきのスキップのテスト（テストしていない一覧）にする
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="context">生成の情報（要手動・生成不可の項目をここに集める）</param>
    /// <param name="sut">テスト対象のインスタンスの作り方</param>
    /// <returns>テストの一覧（テスト名の重複は除く）</returns>
    private static List<PlannedTest> PlanTests(
        INamedTypeSymbol targetType, GenerationContext context, out SutPlan sut)
    {
        sut = SutPlan.Create(targetType, context);
        var members = TestTargets.Collect(targetType)
            .Where(context.Options.Includes)
            .OrderBy(m => m.MethodKind == MethodKind.Constructor ? 0 : 1);

        // テスト名は重複検出のキーにもなるため、同名になるものは先勝ちにして、残りはスキップのテストの理由にする
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tests = new List<PlannedTest>();
        foreach (var member in members)
        {
            var plan = MemberPlan.Create(member, targetType, sut, context.Compilation, context.Options);
            context.ManualItems.AddRange(plan.ManualItems);
            context.NotGeneratableItems.AddRange(plan.NotGeneratableItems);

            var reasons = plan.Reasons.ToList();
            foreach (var test in plan.Tests)
            {
                if (names.Add(test.TestName)) tests.Add(test);
                else reasons.Add($"テスト名 {test.TestName} が重複するため生成していません");
            }

            if (0 < reasons.Count && names.Add(TestTargets.GetBaseName(member) + TestTargets.NotTestedSuffix))
            {
                tests.Add(new NotTestedTestCase(member, reasons));
            }
        }

        return tests;
    }

    /// <summary>
    /// テスト側から見えない名前空間の using を作る（System系を先頭、残りは名前順）
    /// </summary>
    /// <param name="namespaces">必要な名前空間</param>
    /// <param name="testNamespace">テストクラスを置く名前空間</param>
    /// <returns>usingディレクティブ</returns>
    private static UsingDirectiveSyntax[] BuildUsings(IEnumerable<string> namespaces, string? testNamespace)
    {
        // テスト側の名前空間と同じか、その親の名前空間であれば、usingなしで参照できる
        var test = testNamespace ?? string.Empty;
        return namespaces
            .Where(n => n != test && !test.StartsWith(n + ".", StringComparison.Ordinal))
            .OrderBy(n => n == "System" || n.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Select(n => UsingDirective(ParseName(n)))
            .ToArray();
    }

    /// <summary>
    /// 指定の名前空間でテストクラスを包む（名前空間が空ならそのまま返す）
    /// </summary>
    /// <param name="testNamespace">テストクラスを置く名前空間</param>
    /// <param name="testClass">テストクラスの宣言</param>
    /// <returns>名前空間で包んだメンバー</returns>
    private static MemberDeclarationSyntax WrapInNamespace(string? testNamespace, ClassDeclarationSyntax testClass)
    {
        if (string.IsNullOrEmpty(testNamespace)) return testClass;

        return FileScopedNamespaceDeclaration(ParseName(testNamespace!))
            .AddMembers(testClass);
    }

    /// <summary>
    /// NormalizeWhitespace の結果に、メンバー間の空行・末尾の改行を加える
    /// </summary>
    /// <param name="compilationUnit">整形済みのファイル</param>
    /// <returns>整形したファイル</returns>
    private static CompilationUnitSyntax Format(CompilationUnitSyntax compilationUnit)
    {
        // NormalizeWhitespace は null! を「null !」と出力するため、演算子の前の空白を取り除く
        compilationUnit = compilationUnit.ReplaceNodes(
            compilationUnit.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Where(n => n.IsKind(SyntaxKind.SuppressNullableWarningExpression)),
            (_, rewritten) => rewritten.WithOperand(rewritten.Operand.WithoutTrailingTrivia()));

        // TheoryData の1行（{ 入力, 期待値 }）は、NormalizeWhitespace が縦に展開するため1行にまとめる
        // （行の中の配列・List の初期化子も1行にする）
        var theoryDataRows = compilationUnit.DescendantNodes().OfType<InitializerExpressionSyntax>()
            .Where(n => n.IsKind(SyntaxKind.CollectionInitializerExpression) && n.Parent is ObjectCreationExpressionSyntax { Type: GenericNameSyntax { Identifier.ValueText: "TheoryData" } })
            .SelectMany(n => n.Expressions);
        compilationUnit = compilationUnit.ReplaceNodes(theoryDataRows, (original, _) => original is InitializerExpressionSyntax row
            ? ToSingleLine(row)
            : (ExpressionSyntax)new InitializerCompactor().Visit(original)!);

        var testClass = compilationUnit.DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        // NormalizeWhitespace はメソッド同士の間にだけ空行を入れるため、空行がない箇所（フィールドの後など）に補う
        var members = testClass.Members.Select((m, i) => i == 0 || EndsWithBlankLine(testClass.Members[i - 1])
            ? m
            : m.WithLeadingTrivia(m.GetLeadingTrivia().Insert(0, CarriageReturnLineFeed)));
        var formattedClass = testClass
            .WithMembers(List(members));

        // file-scoped namespace の宣言とクラスの間に空行を入れる
        if (testClass.Parent is FileScopedNamespaceDeclarationSyntax)
        {
            formattedClass = formattedClass.WithLeadingTrivia(formattedClass.GetLeadingTrivia().Insert(0, CarriageReturnLineFeed));
        }

        return compilationUnit.ReplaceNode(testClass, formattedClass).WithTrailingTrivia(CarriageReturnLineFeed);
    }

    /// <summary>
    /// { a, b, c } の形の初期化子を1行にする（前後の改行・インデントはそのまま）
    /// </summary>
    /// <param name="initializer">初期化子</param>
    /// <returns>1行にした初期化子</returns>
    private static InitializerExpressionSyntax ToSingleLine(InitializerExpressionSyntax initializer)
    {
        var expressions = initializer.Expressions.Select(e => (ExpressionSyntax)new InitializerCompactor().Visit(e.WithoutTrivia()));
        var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, initializer.Expressions.Count - 1));
        return initializer
            .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(initializer.OpenBraceToken.LeadingTrivia).WithTrailingTrivia(Space))
            .WithExpressions(SeparatedList(expressions, separators))
            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(Space).WithTrailingTrivia(initializer.CloseBraceToken.TrailingTrivia));
    }

    /// <summary>
    /// 配列・List の初期化子（new int[] { 1 } など）を1行に詰める
    /// </summary>
    private sealed class InitializerCompactor : CSharpSyntaxRewriter
    {
        /// <summary>
        /// 初期化子を「{ a, b }」の形にする
        /// </summary>
        /// <param name="node">初期化子</param>
        /// <returns>1行にした初期化子</returns>
        public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
        {
            var visited = (InitializerExpressionSyntax)base.VisitInitializerExpression(node)!;
            var expressions = visited.Expressions.Select(e => e.WithoutTrivia());
            var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, visited.Expressions.Count - 1));
            return visited
                .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(Space))
                .WithExpressions(SeparatedList(expressions, separators))
                .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(Space));
        }

        /// <summary>
        /// 「new int[]」と初期化子の間を空白1つにする
        /// </summary>
        /// <param name="node">配列の生成式</param>
        /// <returns>詰めた生成式</returns>
        public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
        {
            var visited = (ArrayCreationExpressionSyntax)base.VisitArrayCreationExpression(node)!;
            return visited.Initializer is null ? visited : visited.WithType(visited.Type.WithTrailingTrivia(Space));
        }

        /// <summary>
        /// 「new List&lt;T&gt;」と初期化子の間を空白1つにする
        /// </summary>
        /// <param name="node">オブジェクトの生成式</param>
        /// <returns>詰めた生成式</returns>
        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
            if (visited.Initializer is null) return visited;

            return visited.ArgumentList is { } arguments
                ? visited.WithArgumentList(arguments.WithTrailingTrivia(Space))
                : visited.WithType(visited.Type.WithTrailingTrivia(Space));
        }
    }

    /// <summary>
    /// メンバーの後ろに空行（改行2つ）があるかを判定する
    /// </summary>
    /// <param name="member">メンバー</param>
    /// <returns>空行があればtrue</returns>
    private static bool EndsWithBlankLine(MemberDeclarationSyntax member)
    {
        return 2 <= member.GetTrailingTrivia().Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
    }
}
