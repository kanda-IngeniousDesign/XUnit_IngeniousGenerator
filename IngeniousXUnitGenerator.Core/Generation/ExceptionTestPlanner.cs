using IngeniousXUnitGenerator.Core.Guards;
using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// ガードから、確実に正しい例外テストだけを計画し、それ以外は要手動として記録する
/// </summary>
internal sealed class ExceptionTestPlanner
{
    private readonly Compilation _compilation;
    private readonly SutPlan _sut;
    private readonly List<string> _manualItems;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="compilation">対象のコンパイル</param>
    /// <param name="sut">テスト対象のインスタンスの作り方</param>
    /// <param name="manualItems">要手動項目の追加先</param>
    public ExceptionTestPlanner(Compilation compilation, SutPlan sut, List<string> manualItems)
    {
        _compilation = compilation;
        _sut = sut;
        _manualItems = manualItems;
    }

    /// <summary>
    /// メソッド・コンストラクタ1つ分の例外テストを計画する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>例外テストの一覧</returns>
    public IReadOnlyList<ExceptionTestCase> Plan(IMethodSymbol member)
    {
        var declaration = MemberDisplay.FindDeclaration(member);
        if (declaration is null) return [];

        var guards = GuardExtractor.Extract(declaration, _compilation.GetSemanticModel(declaration.SyntaxTree), member);
        var display = MemberDisplay.Of(member);
        if (0 < guards.RemainingThrowCount)
        {
            _manualItems.Add($"{display}: ガードより後ろにある throw {guards.RemainingThrowCount} 件は、前提状態が分からないため対象外です");
        }

        if (guards.Statements.Count == 0) return [];

        var blocker = FindBlocker(member, declaration);
        if (blocker is not null)
        {
            _manualItems.Add($"{display}: {blocker}");
            return [];
        }

        return PlanStatements(member, guards.Statements, display);
    }

    /// <summary>
    /// メンバー全体として例外テストを作れない理由を探す
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <param name="declaration">宣言</param>
    /// <returns>作れない理由（作れる場合はnull）</returns>
    private string? FindBlocker(IMethodSymbol member, BaseMethodDeclarationSyntax declaration)
    {
        if (member.IsGenericMethod) return "ジェネリックメソッドは型引数を決められないため対象外です";
        if (member.Parameters.Any(p => p.RefKind != RefKind.None)) return "ref / out / in 引数を持つメソッドは対象外です";
        if (declaration.Body!.DescendantNodes().OfType<YieldStatementSyntax>().Any()) return "イテレーター（yield）は列挙するまでガードが動かないため対象外です";
        if (member.IsAsync && member.ReturnsVoid) return "async void は例外を捕捉できないため対象外です";
        if (member.MethodKind == MethodKind.Constructor && member.ContainingType.IsAbstract) return "抽象クラスのコンストラクタは直接呼べないため対象外です";
        if (member.MethodKind == MethodKind.Ordinary && !member.IsStatic && _sut.Constructor is null)
        {
            return $"テスト対象のインスタンスを生成できないため対象外です（{_sut.Blocker}）";
        }

        return null;
    }

    /// <summary>
    /// ガード文を先頭から順に計画する（解析できないガードが出たら、それ以降は値を決められないため打ち切る）
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <param name="statements">ガード文</param>
    /// <param name="display">レポート用の表示名</param>
    /// <returns>例外テストの一覧</returns>
    private List<ExceptionTestCase> PlanStatements(IMethodSymbol member, IReadOnlyList<GuardStatement> statements, string display)
    {
        var tests = new List<ExceptionTestCase>();
        for (var i = 0; i < statements.Count; i++)
        {
            if (!statements[i].IsRecognized)
            {
                _manualItems.Add($"{display}: 条件 '{statements[i].UnrecognizedText}' を解析できないため、これ以降のガードは対象外です");
                break;
            }

            foreach (var guardCase in statements[i].Cases)
            {
                var test = TryPlanCase(member, statements, i, guardCase, display);
                if (test is not null) tests.Add(test);
            }
        }

        return tests;
    }

    /// <summary>
    /// ガードの1ケースについて、例外テストを計画する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <param name="statements">ガード文</param>
    /// <param name="index">対象のガード文の位置</param>
    /// <param name="guardCase">対象のケース</param>
    /// <param name="display">レポート用の表示名</param>
    /// <returns>例外テスト（作らない場合はnull）</returns>
    private ExceptionTestCase? TryPlanCase(
        IMethodSymbol member, IReadOnlyList<GuardStatement> statements, int index, GuardCase guardCase, string display)
    {
        var target = guardCase.Parameter;
        var violating = guardCase.Condition.GetViolatingValue();
        if (!ValueSolver.IsRepresentable(violating, target.Type)) return null;

        // 先に評価される条件で例外になる値は、そちらのガードのテストになるため重複して作らない
        if (GetEarlierConditions(statements, index, guardCase).Any(c => c.IsViolatedBy(violating))) return null;

        var arguments = new List<(TestValue, ITypeSymbol)>(member.Parameters.Length);
        foreach (var parameter in member.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter, target))
            {
                arguments.Add((violating, parameter.Type));
                continue;
            }

            // 他の引数は、対象のガードまでに評価される条件をすべて通過する値にする
            var valid = ValueSolver.FindValid(parameter.Type, GetConditionsUpTo(statements, index, parameter));
            if (valid is null)
            {
                _manualItems.Add($"{display}: 引数 {parameter.Name} の有効な値を決められないため、{target.Name} の{violating.ConditionName}のテストは対象外です");
                return null;
            }

            arguments.Add((valid, parameter.Type));
        }

        var testName = $"{MemberDisplay.GetTestName(member)}_{ToPascalCase(target.Name)}{violating.ConditionName}_Throws{guardCase.ExceptionType.Name}";
        return new ExceptionTestCase(member, testName, guardCase.ExceptionType, guardCase.ParamName, arguments);
    }

    /// <summary>
    /// 対象のケースより前に評価される、同じ引数の条件を列挙する
    /// </summary>
    /// <param name="statements">ガード文</param>
    /// <param name="index">対象のガード文の位置</param>
    /// <param name="guardCase">対象のケース</param>
    /// <returns>先に評価される条件</returns>
    private static IEnumerable<GuardCondition> GetEarlierConditions(IReadOnlyList<GuardStatement> statements, int index, GuardCase guardCase)
    {
        var sameStatement = statements[index].Cases.TakeWhile(c => !ReferenceEquals(c, guardCase));
        return statements.Take(index).SelectMany(s => s.Cases)
            .Concat(sameStatement)
            .Where(c => SymbolEqualityComparer.Default.Equals(c.Parameter, guardCase.Parameter))
            .Select(c => c.Condition);
    }

    /// <summary>
    /// 対象のガード文までに評価される、指定の引数の条件を返す
    /// </summary>
    /// <param name="statements">ガード文</param>
    /// <param name="index">対象のガード文の位置</param>
    /// <param name="parameter">引数</param>
    /// <returns>条件の一覧</returns>
    private static List<GuardCondition> GetConditionsUpTo(IReadOnlyList<GuardStatement> statements, int index, IParameterSymbol parameter)
    {
        return statements.Take(index + 1).SelectMany(s => s.Cases)
            .Where(c => SymbolEqualityComparer.Default.Equals(c.Parameter, parameter))
            .Select(c => c.Condition)
            .ToList();
    }

    /// <summary>
    /// 引数名をテスト名用に先頭大文字にする（先頭の _ と @ は除く）
    /// </summary>
    /// <param name="name">引数名</param>
    /// <returns>先頭大文字の名前</returns>
    private static string ToPascalCase(string name)
    {
        var trimmed = name.TrimStart('_', '@');
        return trimmed.Length == 0 ? name : char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }
}
