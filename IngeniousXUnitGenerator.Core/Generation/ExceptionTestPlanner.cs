using IngeniousXUnitGenerator.Core.Guards;
using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// ガードから、確実に正しい例外テストだけを計画し、それ以外は要手動として記録する
/// </summary>
internal sealed class ExceptionTestPlanner
{
    private readonly GenerationContext _context;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="context">生成の情報</param>
    public ExceptionTestPlanner(GenerationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// ガード文を先頭から順に計画する（解析できないガードが出たら、それ以降は値を決められないため打ち切る）
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <param name="statements">ガード文</param>
    /// <param name="display">レポート用の表示名</param>
    /// <returns>例外テストの一覧</returns>
    public List<ExceptionTestCase> Plan(IMethodSymbol member, IReadOnlyList<GuardStatement> statements, string display)
    {
        var tests = new List<ExceptionTestCase>();
        for (var i = 0; i < statements.Count; i++)
        {
            if (!statements[i].IsRecognized)
            {
                _context.ManualItems.Add($"{display}: 条件 '{statements[i].UnrecognizedText}' を解析できないため、これ以降のガードは対象外です");
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
            var valid = ValueSolver.FindValid(parameter.Type, GetConditionsUpTo(statements, index, parameter), _context.Options.UseMoq);
            if (valid is null)
            {
                _context.ManualItems.Add($"{display}: 引数 {parameter.Name} の有効な値を決められないため、{target.Name} の{violating.ConditionName}のテストは対象外です");
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
