using IngeniousXUnitGenerator.Core.Guards;
using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// メソッド本体の比較・パターンから境界値を読み取り、戻り値テストの入力を作る
/// </summary>
internal static class BoundaryInputGenerator
{
    private const int MaxRows = 20;

    /// <summary>
    /// 基準の入力と、引数を1つずつ境界値に変えた入力を作る
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="guards">先頭のガード</param>
    /// <returns>入力の一覧（基準の値を作れない引数があればnull）</returns>
    public static List<object?[]>? Generate(IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, MemberGuards guards)
    {
        var guardConditions = guards.Statements.Where(s => s.IsRecognized).SelectMany(s => s.Cases).ToList();
        var boundaries = CollectBoundaryConstants(declaration, model, member);

        var baseRow = new object?[member.Parameters.Length];
        var variations = new List<object?>[member.Parameters.Length];
        for (var i = 0; i < member.Parameters.Length; i++)
        {
            var parameter = member.Parameters[i];
            var conditions = guardConditions
                .Where(c => SymbolEqualityComparer.Default.Equals(c.Parameter, parameter))
                .Select(c => c.Condition)
                .ToList();
            var valid = FindBaseValue(parameter.Type, conditions);
            if (valid is null) return null;

            baseRow[i] = ToClrValue(valid, parameter.Type);

            // ThrowIfGreaterThan(value, 100) などのガードの定数も、通過する側の境界（100 など）として使う
            boundaries.TryGetValue(parameter, out var constants);
            var guardConstants = conditions.Where(c => c.Kind == GuardConditionKind.Compare).Select(c => (object)c.Constant);
            variations[i] = GetVariations(parameter.Type, [.. constants ?? [], .. guardConstants], conditions);
        }

        return CombineOneAtATime(baseRow, variations);
    }

    /// <summary>
    /// 基準の値を選ぶ（数値は 0 だと掛け算などの結果がすべて 0 になり確認にならないため、1 を優先する）
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <returns>基準の値（ガードを通過する値がなければnull）</returns>
    private static TestValue? FindBaseValue(ITypeSymbol type, List<GuardCondition> conditions)
    {
        var one = TestValue.FromNumber(1);
        if (ConditionParser.IsNumeric(type) && ValueSolver.IsRepresentable(one, type) && !conditions.Any(c => c.IsViolatedBy(one))) return one;

        return ValueSolver.FindValid(type, conditions);
    }

    /// <summary>
    /// 基準の入力から、引数を1つずつ変えた入力を作る（重複は除き、件数に上限を設ける）
    /// </summary>
    /// <param name="baseRow">基準の入力</param>
    /// <param name="variations">引数ごとの境界値</param>
    /// <returns>入力の一覧</returns>
    private static List<object?[]> CombineOneAtATime(object?[] baseRow, List<object?>[] variations)
    {
        var rows = new List<object?[]> { baseRow };
        for (var i = 0; i < variations.Length; i++)
        {
            foreach (var value in variations[i])
            {
                var row = (object?[])baseRow.Clone();
                row[i] = value;
                if (!rows.Any(r => r.SequenceEqual(row))) rows.Add(row);
            }
        }

        return rows.Take(MaxRows).ToList();
    }

    /// <summary>
    /// 引数の型ごとに、境界値の候補を作る（ガードで例外になる値は除く）
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="constants">本体でその引数と比べている定数</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <returns>境界値の候補</returns>
    private static List<object?> GetVariations(ITypeSymbol type, List<object> constants, List<GuardCondition> conditions)
    {
        IEnumerable<TestValue> candidates = type.SpecialType switch
        {
            SpecialType.System_Boolean => [TestValue.False],
            SpecialType.System_String => constants.OfType<string>().Append(string.Empty).Select(TestValue.FromString),
            _ => GetNumberCandidates(constants),
        };

        var values = candidates
            .Where(v => ValueSolver.IsRepresentable(v, type) && !conditions.Any(c => c.IsViolatedBy(v)))
            .Select(v => ToClrValue(v, type))
            .ToList();
        if (type.SpecialType == SpecialType.System_Boolean) values.Add(true);
        return values;
    }

    /// <summary>
    /// 数値の境界値（0 と、比べている定数とその前後）を作る。比較がなければ代表値（0, -1）を使う
    /// </summary>
    /// <param name="constants">本体でその引数と比べている定数</param>
    /// <returns>数値の候補</returns>
    private static IEnumerable<TestValue> GetNumberCandidates(List<object> constants)
    {
        var numbers = constants.Where(c => c is not string and not bool and not char)
            .Select(c => Convert.ToDecimal(c, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (numbers.Count == 0) return [TestValue.FromNumber(0), TestValue.FromNumber(-1)];

        return numbers.SelectMany(c => new[] { c - 1, c, c + 1 }).Prepend(0).Distinct().OrderBy(n => n).Select(TestValue.FromNumber);
    }

    /// <summary>
    /// 本体で引数と比べている定数（比較演算・定数パターン・関係パターン）を集める
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド</param>
    /// <returns>引数ごとの定数</returns>
    private static Dictionary<IParameterSymbol, List<object>> CollectBoundaryConstants(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member)
    {
        var result = new Dictionary<IParameterSymbol, List<object>>(SymbolEqualityComparer.Default);
        foreach (var node in declaration.DescendantNodes())
        {
            var (subject, constantExpression) = node switch
            {
                BinaryExpressionSyntax binary when IsComparison(binary) => SplitComparison(binary, model),
                ConstantPatternSyntax constantPattern => (GetPatternSubject(constantPattern), constantPattern.Expression),
                RelationalPatternSyntax relational => (GetPatternSubject(relational), relational.Expression),
                _ => (null, null),
            };
            if (subject is null || constantExpression is null) continue;

            var parameter = ConditionParser.GetParameter(subject, model, member);
            var constant = model.GetConstantValue(constantExpression);
            if (parameter is null || !constant.HasValue || constant.Value is null) continue;

            if (!result.TryGetValue(parameter, out var list)) result[parameter] = list = [];
            if (!list.Contains(constant.Value)) list.Add(constant.Value);
        }

        return result;
    }

    /// <summary>
    /// 比較演算（==, !=, &lt;, &lt;=, &gt;, &gt;=）かを判定する
    /// </summary>
    /// <param name="binary">二項演算の式</param>
    /// <returns>比較演算ならtrue</returns>
    private static bool IsComparison(BinaryExpressionSyntax binary)
    {
        return binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;
    }

    /// <summary>
    /// 比較演算を「対象の式」と「定数の式」に分ける（定数が左辺でもよい）
    /// </summary>
    /// <param name="binary">比較演算の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>対象の式と定数の式</returns>
    private static (ExpressionSyntax? Subject, ExpressionSyntax? Constant) SplitComparison(BinaryExpressionSyntax binary, SemanticModel model)
    {
        if (model.GetConstantValue(binary.Right).HasValue) return (binary.Left, binary.Right);
        if (model.GetConstantValue(binary.Left).HasValue) return (binary.Right, binary.Left);
        return (null, null);
    }

    /// <summary>
    /// パターンが照合する対象の式（switch 式・is 式の左辺）を求める
    /// </summary>
    /// <param name="pattern">パターン</param>
    /// <returns>対象の式（求められなければnull）</returns>
    private static ExpressionSyntax? GetPatternSubject(PatternSyntax pattern)
    {
        for (SyntaxNode? node = pattern.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case SwitchExpressionSyntax switchExpression:
                    return switchExpression.GoverningExpression;
                case IsPatternExpressionSyntax isPattern:
                    return isPattern.Expression;
                case PatternSyntax or SwitchExpressionArmSyntax:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// テストの値を、引数の型の .NET の値にする
    /// </summary>
    /// <param name="value">テストの値</param>
    /// <param name="type">引数の型</param>
    /// <returns>.NET の値</returns>
    private static object? ToClrValue(TestValue value, ITypeSymbol type)
    {
        return value.Kind switch
        {
            TestValueKind.String => value.Text,
            TestValueKind.False => false,
            TestValueKind.Number => type.SpecialType switch
            {
                SpecialType.System_Int32 => (int)value.Number,
                SpecialType.System_Int64 => (long)value.Number,
                SpecialType.System_Double => (double)value.Number,
                _ => value.Number,
            },
            _ => null,
        };
    }
}
