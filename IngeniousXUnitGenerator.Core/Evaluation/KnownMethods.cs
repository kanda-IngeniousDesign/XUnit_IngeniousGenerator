using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Evaluation;

/// <summary>
/// 入力だけで結果が決まる標準のメソッド・プロパティを評価する
/// </summary>
internal static class KnownMethods
{
    /// <summary>
    /// 標準メソッド（string.IsNullOrEmpty, Math.Abs など）の呼び出しを評価する
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">引数を評価する処理</param>
    /// <returns>呼び出しの結果</returns>
    public static object? Invoke(InvocationExpressionSyntax invocation, SemanticModel model, Func<ExpressionSyntax, object?> evaluate)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { IsStatic: true } method)
        {
            throw new UnsupportedSyntaxException($"メソッド呼び出し '{invocation.Expression}'");
        }

        var arguments = invocation.ArgumentList.Arguments.Select(a => evaluate(a.Expression)).ToList();
        return (method.ContainingType.ToDisplayString(), method.Name, arguments.Count) switch
        {
            ("string", "IsNullOrEmpty", 1) => string.IsNullOrEmpty((string?)arguments[0]),
            ("string", "IsNullOrWhiteSpace", 1) => string.IsNullOrWhiteSpace((string?)arguments[0]),
            ("System.Math", "Abs", 1) => Abs(arguments[0]),
            ("System.Math", "Max", 2) => ClrOperations.CompareNumbers(arguments[0]!, arguments[1]!) < 0 ? arguments[1] : arguments[0],
            ("System.Math", "Min", 2) => ClrOperations.CompareNumbers(arguments[1]!, arguments[0]!) < 0 ? arguments[1] : arguments[0],
            _ => throw new UnsupportedSyntaxException($"メソッド呼び出し '{invocation.Expression}'"),
        };
    }

    /// <summary>
    /// 標準のプロパティ（string.Length）を評価する
    /// </summary>
    /// <param name="memberAccess">メンバーアクセス式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">レシーバーを評価する処理</param>
    /// <returns>プロパティの値</returns>
    public static object? ReadMember(MemberAccessExpressionSyntax memberAccess, SemanticModel model, Func<ExpressionSyntax, object?> evaluate)
    {
        if (model.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol { Name: "Length", ContainingType.SpecialType: SpecialType.System_String })
        {
            // null の Length は実行時に NullReferenceException になる
            return evaluate(memberAccess.Expression) is string s ? s.Length : throw new GuestThrowException();
        }

        throw new UnsupportedSyntaxException($"メンバー '{memberAccess}' の参照");
    }

    /// <summary>
    /// Math.Abs を型ごとに評価する（int.MinValue などは実行時と同じく例外になる）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>絶対値</returns>
    private static object Abs(object? value)
    {
        return value switch
        {
            int i => Math.Abs(i),
            long l => Math.Abs(l),
            double d => Math.Abs(d),
            decimal m => Math.Abs(m),
            _ => throw new UnsupportedSyntaxException("Math.Abs の引数の型"),
        };
    }
}
