using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace XUnit_IngeniousGenerator.Core.Values;

/// <summary>
/// 引数の型に応じた端の値（int.MinValue / int.MaxValue、null など）。
/// コードに比較がなくても、型として受け取りうる値で今の挙動を記録するために使う
/// </summary>
public static class TypeExtremes
{
    /// <summary>
    /// 引数に渡す、型の端の値を返す
    /// </summary>
    /// <param name="parameter">引数</param>
    /// <param name="checkOverflow">オーバーフローを検査するコンパイル設定か（整数の計算結果が変わるため、整数の端の値は使わない）</param>
    /// <returns>端の値</returns>
    public static IReadOnlyList<object?> For(IParameterSymbol parameter, bool checkOverflow)
    {
        return parameter.Type.SpecialType switch
        {
            SpecialType.System_Int32 when !checkOverflow => [int.MinValue, int.MaxValue],
            SpecialType.System_Int64 when !checkOverflow => [long.MinValue, long.MaxValue],
            SpecialType.System_Double => [double.MinValue, double.MaxValue],
            SpecialType.System_Decimal => [decimal.MinValue, decimal.MaxValue],
            SpecialType.System_String => [null],
            _ => [],
        };
    }

    /// <summary>
    /// 型の端の値なら、int.MaxValue のような読みやすい式にする
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>式（端の値でなければnull）</returns>
    public static ExpressionSyntax? TryRender(object? value)
    {
        var (keyword, member) = Classify(value);
        return member is null
            ? null
            : MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, PredefinedType(Token(keyword)), IdentifierName(member));
    }

    /// <summary>
    /// 型の端の値なら、テスト名の条件部分（IsMinValue / IsMaxValue）にする
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>条件部分（端の値でなければnull）</returns>
    public static string? Describe(object? value)
    {
        var member = Classify(value).Member;
        return member is null ? null : "Is" + member;
    }

    /// <summary>
    /// 値がどの型の MinValue / MaxValue かを判定する
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>型のキーワードとメンバー名（端の値でなければメンバー名がnull）</returns>
    private static (SyntaxKind Keyword, string? Member) Classify(object? value)
    {
        return value switch
        {
            int.MinValue => (SyntaxKind.IntKeyword, "MinValue"),
            int.MaxValue => (SyntaxKind.IntKeyword, "MaxValue"),
            long.MinValue => (SyntaxKind.LongKeyword, "MinValue"),
            long.MaxValue => (SyntaxKind.LongKeyword, "MaxValue"),
            double.MinValue => (SyntaxKind.DoubleKeyword, "MinValue"),
            double.MaxValue => (SyntaxKind.DoubleKeyword, "MaxValue"),
            decimal d when d == decimal.MinValue => (SyntaxKind.DecimalKeyword, "MinValue"),
            decimal d when d == decimal.MaxValue => (SyntaxKind.DecimalKeyword, "MaxValue"),
            _ => (SyntaxKind.None, null),
        };
    }
}
