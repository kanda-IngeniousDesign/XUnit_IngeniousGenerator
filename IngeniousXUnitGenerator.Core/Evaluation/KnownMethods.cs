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
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            throw new UnsupportedSyntaxException($"メソッド呼び出し '{invocation.Expression}'");
        }

        if (!method.IsStatic)
        {
            if (method.ContainingType.SpecialType != SpecialType.System_String || invocation.Expression is not MemberAccessExpressionSyntax receiver)
            {
                throw new UnsupportedSyntaxException($"メソッド呼び出し '{invocation.Expression}'");
            }

            // null のメソッド呼び出しは実行時に NullReferenceException になる
            var text = evaluate(receiver.Expression) as string ?? throw new GuestThrowException();
            return StringMethods.Invoke(text, method, invocation, model, evaluate);
        }

        var arguments = invocation.ArgumentList.Arguments.Select(a => evaluate(a.Expression)).ToList();
        return (method.ContainingType.ToDisplayString(), method.Name, arguments.Count) switch
        {
            ("string", "IsNullOrEmpty", 1) => string.IsNullOrEmpty((string?)arguments[0]),
            ("string", "IsNullOrWhiteSpace", 1) => string.IsNullOrWhiteSpace((string?)arguments[0]),
            ("System.Math", "Abs", 1) => Abs(arguments[0]),
            ("System.Math", "Max", 2) => ClrOperations.CompareNumbers(arguments[0]!, arguments[1]!) < 0 ? arguments[1] : arguments[0],
            ("System.Math", "Min", 2) => ClrOperations.CompareNumbers(arguments[1]!, arguments[0]!) < 0 ? arguments[1] : arguments[0],
            ("System.Math", "Floor", 1) => Rounding(arguments[0], Math.Floor, Math.Floor),
            ("System.Math", "Ceiling", 1) => Rounding(arguments[0], Math.Ceiling, Math.Ceiling),
            ("System.Math", "Round", 1) => Rounding(arguments[0], Math.Round, Math.Round),
            ("System.Math", "Round", 2) when arguments[1] is int digits => Rounding(arguments[0], d => Math.Round(d, digits), m => Math.Round(m, digits)),
            ("string", "Equals", 2) when method.Parameters[0].Type.SpecialType == SpecialType.System_String =>
                string.Equals((string?)arguments[0], (string?)arguments[1], StringComparison.Ordinal),
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
    /// Math.Floor / Ceiling / Round を double・decimal ごとに評価する（Round は C# と同じく偶数丸め）
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="forDouble">double の場合の計算</param>
    /// <param name="forDecimal">decimal の場合の計算</param>
    /// <returns>計算結果</returns>
    private static object Rounding(object? value, Func<double, double> forDouble, Func<decimal, decimal> forDecimal)
    {
        return value switch
        {
            double d => forDouble(d),
            decimal m => forDecimal(m),
            _ => throw new UnsupportedSyntaxException("Math の丸めの引数の型"),
        };
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
