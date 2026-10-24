using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// 結果がカルチャに依存しない string のインスタンスメソッドを評価する
/// （ToUpper() や StringComparison を指定しない StartsWith などは、実行環境のカルチャで結果が変わるため対象外）
/// </summary>
internal static class StringMethods
{
    /// <summary>
    /// string のインスタンスメソッドを評価する
    /// </summary>
    /// <param name="text">レシーバーの文字列</param>
    /// <param name="method">呼び出すメソッド</param>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">引数を評価する処理</param>
    /// <returns>呼び出しの結果</returns>
    public static object? Invoke(
        string text, IMethodSymbol method, InvocationExpressionSyntax invocation, SemanticModel model, Func<ExpressionSyntax, object?> evaluate)
    {
        var arguments = invocation.ArgumentList.Arguments;
        var types = string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()));
        try
        {
            return (method.Name, types) switch
            {
                ("Trim", "") => text.Trim(),
                ("TrimStart", "") => text.TrimStart(),
                ("TrimEnd", "") => text.TrimEnd(),
                ("ToUpperInvariant", "") => text.ToUpperInvariant(),
                ("ToLowerInvariant", "") => text.ToLowerInvariant(),
                ("Substring", "int") => text.Substring((int)evaluate(arguments[0].Expression)!),
                ("Substring", "int,int") => text.Substring((int)evaluate(arguments[0].Expression)!, (int)evaluate(arguments[1].Expression)!),
                ("Contains", "string") => text.Contains((string)evaluate(arguments[0].Expression)!),
                ("Contains", "char") => 0 <= text.IndexOf((char)evaluate(arguments[0].Expression)!),
                ("Replace", "string,string?") or ("Replace", "string,string") =>
                    text.Replace((string)evaluate(arguments[0].Expression)!, (string?)evaluate(arguments[1].Expression)),
                ("Replace", "char,char") => text.Replace((char)evaluate(arguments[0].Expression)!, (char)evaluate(arguments[1].Expression)!),
                ("PadLeft", "int") => text.PadLeft((int)evaluate(arguments[0].Expression)!),
                ("PadLeft", "int,char") => text.PadLeft((int)evaluate(arguments[0].Expression)!, (char)evaluate(arguments[1].Expression)!),
                ("PadRight", "int") => text.PadRight((int)evaluate(arguments[0].Expression)!),
                ("PadRight", "int,char") => text.PadRight((int)evaluate(arguments[0].Expression)!, (char)evaluate(arguments[1].Expression)!),
                ("StartsWith", "char") => 0 < text.Length && text[0] == (char)evaluate(arguments[0].Expression)!,
                ("EndsWith", "char") => 0 < text.Length && text[text.Length - 1] == (char)evaluate(arguments[0].Expression)!,
                ("StartsWith", "string,System.StringComparison") =>
                    text.StartsWith((string)evaluate(arguments[0].Expression)!, GetOrdinalComparison(arguments[1], model)),
                ("EndsWith", "string,System.StringComparison") =>
                    text.EndsWith((string)evaluate(arguments[0].Expression)!, GetOrdinalComparison(arguments[1], model)),
                ("Equals", "string?") or ("Equals", "string") => string.Equals(text, (string?)evaluate(arguments[0].Expression), StringComparison.Ordinal),
                _ => throw new UnsupportedSyntaxException($"メソッド呼び出し '{invocation.Expression}'"),
            };
        }
        catch (ArgumentException ex)
        {
            // Substring の範囲外などは、実行時にも例外になる
            throw new GuestThrowException(null, ex.GetType().FullName, ex.ParamName, invocation);
        }
    }

    /// <summary>
    /// StringComparison の引数が Ordinal / OrdinalIgnoreCase の定数なら、その値を返す（カルチャに依存する比較は対象外）
    /// </summary>
    /// <param name="argument">StringComparison の引数</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>比較の種類</returns>
    private static StringComparison GetOrdinalComparison(ArgumentSyntax argument, SemanticModel model)
    {
        var constant = model.GetConstantValue(argument.Expression);
        if (constant.Value is int value && (StringComparison)value is StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase)
        {
            return (StringComparison)value;
        }

        throw new UnsupportedSyntaxException("カルチャに依存する文字列の比較");
    }
}
