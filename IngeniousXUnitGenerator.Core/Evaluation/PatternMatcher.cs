using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Evaluation;

/// <summary>
/// 定数・関係・論理（and / or / not）パターンを評価する
/// </summary>
internal sealed class PatternMatcher
{
    private readonly SemanticModel _model;

    /// <summary>
    /// パターンの評価を準備する
    /// </summary>
    /// <param name="model">セマンティックモデル</param>
    public PatternMatcher(SemanticModel model)
    {
        _model = model;
    }

    /// <summary>
    /// 値がパターンに合致するかを判定する
    /// </summary>
    /// <param name="pattern">パターン</param>
    /// <param name="value">値</param>
    /// <returns>合致すればtrue</returns>
    public bool Matches(PatternSyntax pattern, object? value)
    {
        return pattern switch
        {
            DiscardPatternSyntax => true,
            ParenthesizedPatternSyntax parenthesized => Matches(parenthesized.Pattern, value),
            UnaryPatternSyntax not => !Matches(not.Pattern, value),
            BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.AndPattern) => Matches(binary.Left, value) && Matches(binary.Right, value),
            BinaryPatternSyntax binary => Matches(binary.Left, value) || Matches(binary.Right, value),
            ConstantPatternSyntax constant => ClrOperations.ValuesEqual(value, GetConstant(constant.Expression)),
            RelationalPatternSyntax relational => MatchesRelational(relational, value),
            _ => throw new UnsupportedSyntaxException($"パターン '{pattern}'"),
        };
    }

    /// <summary>
    /// 関係パターン（&lt; 10 など）を評価する
    /// </summary>
    /// <param name="relational">関係パターン</param>
    /// <param name="value">値</param>
    /// <returns>合致すればtrue</returns>
    private bool MatchesRelational(RelationalPatternSyntax relational, object? value)
    {
        if (!ClrOperations.IsNumber(value)) return false;

        var comparison = ClrOperations.CompareNumbers(value!, GetConstant(relational.Expression)!);
        return relational.OperatorToken.Kind() switch
        {
            SyntaxKind.LessThanToken => comparison < 0,
            SyntaxKind.LessThanEqualsToken => comparison <= 0,
            SyntaxKind.GreaterThanToken => 0 < comparison,
            _ => 0 <= comparison,
        };
    }

    /// <summary>
    /// パターンに書かれた定数を取り出す
    /// </summary>
    /// <param name="expression">定数の式</param>
    /// <returns>定数の値</returns>
    private object? GetConstant(ExpressionSyntax expression)
    {
        var constant = _model.GetConstantValue(expression);
        return constant.HasValue ? constant.Value : throw new UnsupportedSyntaxException($"定数でないパターン '{expression}'");
    }
}
