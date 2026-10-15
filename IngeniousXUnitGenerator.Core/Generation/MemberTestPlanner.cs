using IngeniousXUnitGenerator.Core.Guards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// メソッド・コンストラクタ1つ分のテスト（例外テスト・戻り値テスト）を計画する
/// </summary>
internal sealed class MemberTestPlanner
{
    private readonly Compilation _compilation;
    private readonly SutPlan _sut;
    private readonly List<string> _manualItems;
    private readonly ExceptionTestPlanner _exceptionPlanner;
    private readonly ReturnValueTestPlanner _returnValuePlanner;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="compilation">対象のコンパイル</param>
    /// <param name="sut">テスト対象のインスタンスの作り方</param>
    /// <param name="manualItems">要手動項目の追加先</param>
    public MemberTestPlanner(Compilation compilation, SutPlan sut, List<string> manualItems)
    {
        _compilation = compilation;
        _sut = sut;
        _manualItems = manualItems;
        _exceptionPlanner = new ExceptionTestPlanner(manualItems);
        _returnValuePlanner = new ReturnValueTestPlanner(manualItems);
    }

    /// <summary>
    /// メソッド・コンストラクタ1つ分のテストを計画する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>テストの一覧</returns>
    public IReadOnlyList<PlannedTest> Plan(IMethodSymbol member)
    {
        var declaration = MemberDisplay.FindDeclaration(member);
        if (declaration is null) return [];

        var model = _compilation.GetSemanticModel(declaration.SyntaxTree);
        var guards = GuardExtractor.Extract(declaration, model, member);
        var display = MemberDisplay.Of(member);
        if (0 < guards.RemainingThrowCount)
        {
            _manualItems.Add($"{display}: ガードより後ろにある throw {guards.RemainingThrowCount} 件は、前提状態が分からないため例外テストの対象外です");
        }

        var hasReturnValue = member.MethodKind == MethodKind.Ordinary && !member.ReturnsVoid;
        if (guards.Statements.Count == 0 && !hasReturnValue) return [];

        var blocker = FindBlocker(member, declaration);
        if (blocker is not null)
        {
            _manualItems.Add($"{display}: {blocker}");
            return [];
        }

        var tests = new List<PlannedTest>(_exceptionPlanner.Plan(member, guards.Statements, display));
        var returnValueTest = _returnValuePlanner.Plan(member, declaration, model, guards, display);
        if (returnValueTest is not null) tests.Add(returnValueTest);
        return tests;
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
