using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Guards;

/// <summary>
/// ifガードの条件式を、引数ごとの例外条件に分解する
/// </summary>
internal static class ConditionParser
{
    /// <summary>
    /// 条件式を「||」で分解し、すべての項が解析できれば引数ごとの条件を返す
    /// </summary>
    /// <param name="condition">ifの条件式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>引数ごとの条件（1つでも解析できない項があればnull）</returns>
    public static List<(IParameterSymbol Parameter, GuardCondition Condition)>? Parse(
        ExpressionSyntax condition, SemanticModel model, IMethodSymbol member)
    {
        var results = new List<(IParameterSymbol, GuardCondition)>();
        try
        {
            return TryAdd(condition, model, member, results) ? results : null;
        }
        catch (InsufficientExecutionStackException)
        {
            // 入れ子が深すぎる条件は、解析できない条件として扱う（スタックオーバーフローで IDE の解析のプロセスごと落ちないようにする）
            return null;
        }
    }

    /// <summary>
    /// 式が指すのがこのメンバーの引数なら、その引数を返す
    /// </summary>
    /// <param name="expression">判定する式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>引数（引数でなければnull）</returns>
    public static IParameterSymbol? GetParameter(ExpressionSyntax expression, SemanticModel model, IMethodSymbol member)
    {
        return model.GetSymbolInfo(expression).Symbol is IParameterSymbol parameter
            && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, member)
            ? parameter
            : null;
    }

    /// <summary>
    /// 数値の定数なら decimal に変換する
    /// </summary>
    /// <param name="expression">定数かもしれない式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="value">変換した値</param>
    /// <returns>数値の定数ならtrue</returns>
    public static bool TryGetNumericConstant(ExpressionSyntax expression, SemanticModel model, out decimal value)
    {
        value = 0;
        var constant = model.GetConstantValue(expression);
        if (!constant.HasValue || constant.Value is null or bool or char or string) return false;

        try
        {
            value = Convert.ToDecimal(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            // decimal に収まらない値（double の極大値など）は境界値として扱えないため対象外にする
            return false;
        }
    }

    /// <summary>
    /// 数値型（Nullableの中身を含む）かを判定する
    /// </summary>
    /// <param name="type">判定する型</param>
    /// <returns>数値型ならtrue</returns>
    public static bool IsNumeric(ITypeSymbol type)
    {
        return type.SpecialType is >= SpecialType.System_SByte and <= SpecialType.System_Double;
    }

    /// <summary>
    /// 式を1項ずつ解析して結果に追加する
    /// </summary>
    /// <param name="expression">解析する式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <param name="results">解析結果の追加先</param>
    /// <returns>解析できたらtrue</returns>
    private static bool TryAdd(
        ExpressionSyntax expression, SemanticModel model, IMethodSymbol member, List<(IParameterSymbol, GuardCondition)> results)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => TryAdd(parenthesized.Expression, model, member, results),
            BinaryExpressionSyntax logicalOr when logicalOr.IsKind(SyntaxKind.LogicalOrExpression) =>
                TryAdd(logicalOr.Left, model, member, results) && TryAdd(logicalOr.Right, model, member, results),
            IsPatternExpressionSyntax isPattern => TryAddPattern(isPattern.Expression, isPattern.Pattern, false, model, member, results),
            BinaryExpressionSyntax binary => TryAddComparison(binary, model, member, results),
            InvocationExpressionSyntax invocation => TryAddStringCheck(invocation, model, member, results),
            _ => false,
        };
    }

    /// <summary>
    /// 「引数 == null」または「引数 比較演算子 数値定数」を解析する
    /// </summary>
    /// <param name="binary">二項演算の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <param name="results">解析結果の追加先</param>
    /// <returns>解析できたらtrue</returns>
    private static bool TryAddComparison(
        BinaryExpressionSyntax binary, SemanticModel model, IMethodSymbol member, List<(IParameterSymbol, GuardCondition)> results)
    {
        if (!TryGetOperator(binary.Kind(), out var compareOperator)) return false;

        if (compareOperator == CompareOperator.Equal)
        {
            if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression)) return TryAddCondition(binary.Left, GuardCondition.Null, model, member, results);
            if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression)) return TryAddCondition(binary.Right, GuardCondition.Null, model, member, results);
        }

        // 定数が左辺にある書き方（0 > x）は、引数を左辺にした形（x < 0）にそろえる
        if (TryGetNumericConstant(binary.Right, model, out var right))
        {
            return TryAddNumeric(binary.Left, GuardCondition.Compare(compareOperator, right), model, member, results);
        }

        return TryGetNumericConstant(binary.Left, model, out var left)
            && TryAddNumeric(binary.Right, GuardCondition.Compare(Flip(compareOperator), left), model, member, results);
    }

    /// <summary>
    /// 「引数 is パターン」を解析する（null、""、数値定数、&lt; などの比較、or・and・not の組み合わせ）。
    /// 例外になる条件を「または」でつないだ形にできるものだけを対象にする（not は比較の向きを反転し、not (A and B) は not A or not B にする）
    /// </summary>
    /// <param name="subject">is の左辺</param>
    /// <param name="pattern">パターン</param>
    /// <param name="negated">not の内側ならtrue</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <param name="results">解析結果の追加先</param>
    /// <returns>解析できたらtrue</returns>
    private static bool TryAddPattern(
        ExpressionSyntax subject, PatternSyntax pattern, bool negated, SemanticModel model, IMethodSymbol member, List<(IParameterSymbol, GuardCondition)> results)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (pattern)
        {
            case ParenthesizedPatternSyntax parenthesized:
                return TryAddPattern(subject, parenthesized.Pattern, negated, model, member, results);
            case UnaryPatternSyntax not:
                return TryAddPattern(subject, not.Pattern, !negated, model, member, results);
            case BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.OrPattern) != negated:
                // A or B、または not (A and B)（= not A or not B）は、どちらかに当てはまれば例外になる
                return TryAddPattern(subject, binary.Left, negated, model, member, results)
                    && TryAddPattern(subject, binary.Right, negated, model, member, results);
            case ConstantPatternSyntax constant when constant.Expression.IsKind(SyntaxKind.NullLiteralExpression):
                return !negated && TryAddCondition(subject, GuardCondition.Null, model, member, results);
            case ConstantPatternSyntax constant when model.GetConstantValue(constant.Expression) is { HasValue: true, Value: "" }:
                return !negated && GetParameter(subject, model, member) is { Type.SpecialType: SpecialType.System_String }
                    && TryAddCondition(subject, GuardCondition.Empty, model, member, results);
            case ConstantPatternSyntax constant when TryGetNumericConstant(constant.Expression, model, out var value):
                return TryAddNumeric(subject, GuardCondition.Compare(negated ? CompareOperator.NotEqual : CompareOperator.Equal, value), model, member, results);
            case RelationalPatternSyntax relational when TryGetNumericConstant(relational.Expression, model, out var bound)
                && TryGetRelationalOperator(relational.OperatorToken.Kind(), out var compareOperator):
                return TryAddNumeric(subject, GuardCondition.Compare(negated ? Negate(compareOperator) : compareOperator, bound), model, member, results);
            default:
                // A and B（範囲の内側で例外になる）、not (A or B) などは、1つの比較で表せないため対象外にする
                return false;
        }
    }

    /// <summary>
    /// string.IsNullOrEmpty / string.IsNullOrWhiteSpace の呼び出しを解析する
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <param name="results">解析結果の追加先</param>
    /// <returns>解析できたらtrue</returns>
    private static bool TryAddStringCheck(
        InvocationExpressionSyntax invocation, SemanticModel model, IMethodSymbol member, List<(IParameterSymbol, GuardCondition)> results)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { ContainingType.SpecialType: SpecialType.System_String } method) return false;
        if (invocation.ArgumentList.Arguments.Count != 1) return false;

        var parameter = GetParameter(invocation.ArgumentList.Arguments[0].Expression, model, member);
        if (parameter is null) return false;

        switch (method.Name)
        {
            case nameof(string.IsNullOrEmpty):
                results.AddRange([(parameter, GuardCondition.Null), (parameter, GuardCondition.Empty)]);
                return true;
            case nameof(string.IsNullOrWhiteSpace):
                results.AddRange([(parameter, GuardCondition.Null), (parameter, GuardCondition.Empty), (parameter, GuardCondition.WhiteSpace)]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 式が引数なら、条件を追加する
    /// </summary>
    /// <param name="expression">引数かもしれない式</param>
    /// <param name="condition">例外になる条件</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <param name="results">解析結果の追加先</param>
    /// <returns>追加できたらtrue</returns>
    private static bool TryAddCondition(
        ExpressionSyntax expression, GuardCondition condition, SemanticModel model, IMethodSymbol member, List<(IParameterSymbol, GuardCondition)> results)
    {
        var parameter = GetParameter(expression, model, member);
        if (parameter is null) return false;

        results.Add((parameter, condition));
        return true;
    }

    /// <summary>
    /// 式が数値型の引数なら、比較の条件を追加する
    /// </summary>
    /// <param name="expression">引数かもしれない式</param>
    /// <param name="condition">比較の条件</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <param name="results">解析結果の追加先</param>
    /// <returns>追加できたらtrue</returns>
    private static bool TryAddNumeric(
        ExpressionSyntax expression, GuardCondition condition, SemanticModel model, IMethodSymbol member, List<(IParameterSymbol, GuardCondition)> results)
    {
        var parameter = GetParameter(expression, model, member);
        if (parameter is null || !IsNumeric(parameter.Type)) return false;

        results.Add((parameter, condition));
        return true;
    }

    /// <summary>
    /// 構文の種類を比較演算子に変換する
    /// </summary>
    /// <param name="kind">二項演算の構文の種類</param>
    /// <param name="compareOperator">比較演算子</param>
    /// <returns>比較演算子ならtrue</returns>
    private static bool TryGetOperator(SyntaxKind kind, out CompareOperator compareOperator)
    {
        compareOperator = kind switch
        {
            SyntaxKind.EqualsExpression => CompareOperator.Equal,
            SyntaxKind.NotEqualsExpression => CompareOperator.NotEqual,
            SyntaxKind.LessThanExpression => CompareOperator.LessThan,
            SyntaxKind.LessThanOrEqualExpression => CompareOperator.LessThanOrEqual,
            SyntaxKind.GreaterThanExpression => CompareOperator.GreaterThan,
            SyntaxKind.GreaterThanOrEqualExpression => CompareOperator.GreaterThanOrEqual,
            _ => (CompareOperator)(-1),
        };
        return Enum.IsDefined(typeof(CompareOperator), compareOperator);
    }

    /// <summary>
    /// 比較のパターンの記号（&lt; など）を比較演算子に変換する
    /// </summary>
    /// <param name="kind">記号の種類</param>
    /// <param name="compareOperator">比較演算子</param>
    /// <returns>比較の記号ならtrue</returns>
    private static bool TryGetRelationalOperator(SyntaxKind kind, out CompareOperator compareOperator)
    {
        compareOperator = kind switch
        {
            SyntaxKind.LessThanToken => CompareOperator.LessThan,
            SyntaxKind.LessThanEqualsToken => CompareOperator.LessThanOrEqual,
            SyntaxKind.GreaterThanToken => CompareOperator.GreaterThan,
            SyntaxKind.GreaterThanEqualsToken => CompareOperator.GreaterThanOrEqual,
            _ => (CompareOperator)(-1),
        };
        return Enum.IsDefined(typeof(CompareOperator), compareOperator);
    }

    /// <summary>
    /// 条件を反転したときの演算子を返す（not &lt; 0 ⇔ &gt;= 0）
    /// </summary>
    /// <param name="compareOperator">元の演算子</param>
    /// <returns>反転した演算子</returns>
    private static CompareOperator Negate(CompareOperator compareOperator)
    {
        return compareOperator switch
        {
            CompareOperator.Equal => CompareOperator.NotEqual,
            CompareOperator.NotEqual => CompareOperator.Equal,
            CompareOperator.LessThan => CompareOperator.GreaterThanOrEqual,
            CompareOperator.LessThanOrEqual => CompareOperator.GreaterThan,
            CompareOperator.GreaterThan => CompareOperator.LessThanOrEqual,
            _ => CompareOperator.LessThan,
        };
    }

    /// <summary>
    /// 左右を入れ替えたときの演算子を返す（a &lt; b ⇔ b &gt; a）
    /// </summary>
    /// <param name="compareOperator">元の演算子</param>
    /// <returns>左右を入れ替えた演算子</returns>
    private static CompareOperator Flip(CompareOperator compareOperator)
    {
        return compareOperator switch
        {
            CompareOperator.LessThan => CompareOperator.GreaterThan,
            CompareOperator.LessThanOrEqual => CompareOperator.GreaterThanOrEqual,
            CompareOperator.GreaterThan => CompareOperator.LessThan,
            CompareOperator.GreaterThanOrEqual => CompareOperator.LessThanOrEqual,
            _ => compareOperator,
        };
    }
}
