using IngeniousXUnitGenerator.Core.Guards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// メソッド・コンストラクタ1つ分のテスト（例外テスト・戻り値テスト）を計画する
/// </summary>
internal sealed class MemberTestPlanner
{
    private readonly GenerationContext _context;
    private readonly SutPlan _sut;
    private readonly ExceptionTestPlanner _exceptionPlanner;
    private readonly ReturnValueTestPlanner _returnValuePlanner;
    private readonly LaterThrowTestPlanner _laterThrowPlanner;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="context">生成の情報</param>
    /// <param name="sut">テスト対象のインスタンスの作り方</param>
    public MemberTestPlanner(GenerationContext context, SutPlan sut)
    {
        _context = context;
        _sut = sut;
        _exceptionPlanner = new ExceptionTestPlanner(context);
        _returnValuePlanner = new ReturnValueTestPlanner(context);
        _laterThrowPlanner = new LaterThrowTestPlanner(context);
    }

    /// <summary>
    /// メソッド・コンストラクタ1つ分のテストを計画する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>テストの一覧</returns>
    public IReadOnlyList<PlannedTest> Plan(IMethodSymbol member)
    {
        if (member.MethodKind == MethodKind.PropertySet) return PlanSetter(member);

        var declaration = MemberDisplay.FindDeclaration(member);
        if (declaration is null) return [];

        var model = _context.Compilation.GetSemanticModel(declaration.SyntaxTree);
        var guards = GuardExtractor.Extract(declaration, model, member);
        var display = MemberDisplay.Of(member);
        // メソッドは戻り値の有無にかかわらず、戻り値テスト・例外が出ないことの確認の対象にする
        if (guards.Statements.Count == 0 && member.MethodKind != MethodKind.Ordinary) return [];

        var blocker = FindBlocker(member, declaration);
        if (blocker is not null)
        {
            _context.ManualItems.Add($"{display}: {blocker}");
            return [];
        }

        var tests = new List<PlannedTest>(_exceptionPlanner.Plan(member, guards.Statements, display));
        tests.AddRange(_laterThrowPlanner.Plan(member, declaration, model, guards, display));
        var returnValueTest = _returnValuePlanner.Plan(member, declaration, model, guards, display);
        if (returnValueTest is not null) tests.Add(returnValueTest);
        return tests;
    }

    /// <summary>
    /// プロパティの set アクセサーの先頭のガードから、例外テストを計画する
    /// （代入した値の確認は、前提状態や get との対応が分からないため対象外）
    /// </summary>
    /// <param name="member">set アクセサー</param>
    /// <returns>テストの一覧</returns>
    private IReadOnlyList<PlannedTest> PlanSetter(IMethodSymbol member)
    {
        var accessor = member.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<AccessorDeclarationSyntax>().FirstOrDefault();
        if (accessor is null) return [];

        var guards = GuardExtractor.ExtractAccessor(accessor, _context.Compilation.GetSemanticModel(accessor.SyntaxTree), member);
        if (guards.Statements.Count == 0) return [];

        var display = MemberDisplay.Of(member);
        if (!member.IsStatic && _sut.Constructor is null)
        {
            _context.ManualItems.Add($"{display}: テスト対象のインスタンスを生成できないため対象外です（{_sut.Blocker}）");
            return [];
        }

        return _exceptionPlanner.Plan(member, guards.Statements, display);
    }

    /// <summary>
    /// メンバー全体としてテストを作れない理由を探す
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <param name="declaration">宣言</param>
    /// <returns>作れない理由（作れる場合はnull）</returns>
    private string? FindBlocker(IMethodSymbol member, BaseMethodDeclarationSyntax declaration)
    {
        if (member.IsGenericMethod) return "ジェネリックメソッドは型引数を決められないため対象外です";
        if (member.Parameters.Any(p => p.RefKind != RefKind.None)) return "ref / out / in 引数を持つメソッドは対象外です";
        if (declaration.DescendantNodes().OfType<YieldStatementSyntax>().Any()) return "イテレーター（yield）は列挙するまで処理が動かないため対象外です";
        if (member.IsAsync && member.ReturnsVoid) return "async void は例外を捕捉できないため対象外です";
        if (member.MethodKind == MethodKind.Constructor && member.ContainingType.IsAbstract) return "抽象クラスのコンストラクタは直接呼べないため対象外です";
        if (member.MethodKind == MethodKind.Ordinary && !member.IsStatic && _sut.Constructor is null)
        {
            return $"テスト対象のインスタンスを生成できないため対象外です（{_sut.Blocker}）";
        }

        return null;
    }
}
