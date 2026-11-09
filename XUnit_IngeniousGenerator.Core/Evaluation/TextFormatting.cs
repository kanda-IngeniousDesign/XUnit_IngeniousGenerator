using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XUnit_IngeniousGenerator.Core.Values;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// 値を文字列にする処理（文字列補間、string.Join / Concat / Format、ToString()）を評価する。
/// 実行環境のカルチャで結果が変わらない値（string・char・bool・整数・列挙型・null）だけを対象にする
/// </summary>
internal static class TextFormatting
{
    /// <summary>
    /// 値を、カルチャに依存しない文字列にする（double・decimal・日付などは、カルチャで結果が変わるため対象外）
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="type">値の型（列挙型の値を名前にするため）</param>
    /// <returns>文字列</returns>
    public static string ToText(object? value, ITypeSymbol? type)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            char c => c.ToString(),
            bool b => b ? "True" : "False",
            EnumMemberValue member => member.Name,
            int or long when type?.TypeKind == TypeKind.Enum => EnumName(type, value),
            int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCultureConcat")),
        };
    }

    /// <summary>
    /// 文字列補間（$"{name} 様"）を評価する（書式指定 {x:N0} は対象外、幅の指定 {x,5} は対象）
    /// </summary>
    /// <param name="interpolated">文字列補間の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">埋め込む式を評価する処理</param>
    /// <returns>文字列</returns>
    public static string Interpolate(InterpolatedStringExpressionSyntax interpolated, SemanticModel model, Func<ExpressionSyntax, object?> evaluate)
    {
        var builder = new StringBuilder();
        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    builder.Append(text.TextToken.ValueText);
                    break;
                case InterpolationSyntax { FormatClause: null } interpolation:
                    var value = ToText(evaluate(interpolation.Expression), model.GetTypeInfo(interpolation.Expression).Type);
                    builder.Append(Align(value, interpolation.AlignmentClause, model));
                    break;
                default:
                    throw new UnsupportedSyntaxException(Messages.Get("UnsupportedInterpolation"));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// string.Join・string.Concat・string.Format を評価する
    /// </summary>
    /// <param name="name">メソッド名</param>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">引数を評価する処理</param>
    /// <param name="value">結果</param>
    /// <returns>評価できたらtrue</returns>
    public static bool TryInvokeStringStatic(
        string name, InvocationExpressionSyntax invocation, SemanticModel model, Func<ExpressionSyntax, object?> evaluate, out string value)
    {
        value = string.Empty;
        var arguments = invocation.ArgumentList.Arguments;
        var texts = new Func<IEnumerable<string>>(() => arguments.Skip(1).SelectMany(a => Expand(a.Expression, model, evaluate)));
        switch (name)
        {
            case "Join" when 2 <= arguments.Count:
                value = string.Join(ToText(evaluate(arguments[0].Expression), null), texts());
                return true;
            case "Concat" when 1 <= arguments.Count:
                value = string.Concat(arguments.SelectMany(a => Expand(a.Expression, model, evaluate)));
                return true;
            case "Format" when 2 <= arguments.Count && evaluate(arguments[0].Expression) is string format && !HasFormatSpecifier(format):
                value = string.Format(CultureInfo.InvariantCulture, format, texts().Cast<object>().ToArray());
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 引数の値を文字列の並びにする（配列・コレクションなら要素ごと）
    /// </summary>
    /// <param name="expression">引数の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="evaluate">引数を評価する処理</param>
    /// <returns>文字列の並び</returns>
    private static IEnumerable<string> Expand(ExpressionSyntax expression, SemanticModel model, Func<ExpressionSyntax, object?> evaluate)
    {
        var value = evaluate(expression);
        if (value is CollectionValue collection) return collection.Items.Select(i => ToText(i, collection.ElementType));
        return [ToText(value, model.GetTypeInfo(expression).Type)];
    }

    /// <summary>
    /// 書式の文字列に、書式指定（{0:N0} など、カルチャで結果が変わりうるもの）があるかを判定する
    /// </summary>
    /// <param name="format">書式の文字列</param>
    /// <returns>あればtrue</returns>
    private static bool HasFormatSpecifier(string format)
    {
        var inPlaceholder = false;
        foreach (var c in format)
        {
            if (c == '{') inPlaceholder = true;
            else if (c == '}') inPlaceholder = false;
            else if (c == ':' && inPlaceholder) return true;
        }

        return false;
    }

    /// <summary>
    /// 幅の指定（{x,5} は右寄せ、{x,-5} は左寄せ）に合わせて空白を補う
    /// </summary>
    /// <param name="text">文字列</param>
    /// <param name="alignment">幅の指定</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>空白を補った文字列</returns>
    private static string Align(string text, InterpolationAlignmentClauseSyntax? alignment, SemanticModel model)
    {
        if (alignment is null) return text;

        const int MaxWidth = 1000;
        var width = model.GetConstantValue(alignment.Value) is { HasValue: true, Value: int w } ? w : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedInterpolation"));
        if (MaxWidth < Math.Abs(width)) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedLongString", width));
        return width < 0 ? text.PadRight(-width) : text.PadLeft(width);
    }

    /// <summary>
    /// 列挙型の値（整数）を、メンバー名にする（どのメンバーにも当たらなければ数値のまま）
    /// </summary>
    /// <param name="type">列挙型</param>
    /// <param name="value">値</param>
    /// <returns>メンバー名（当たらなければ評価をやめる）</returns>
    private static string EnumName(ITypeSymbol type, object value)
    {
        var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        var member = type.GetMembers().OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.HasConstantValue && Convert.ToInt64(f.ConstantValue, CultureInfo.InvariantCulture) == number);
        // フラグの組み合わせ（"A, B"）などメンバーに当たらない値は、.NET と同じ文字列を確実には作れないため対象外にする
        return member?.Name ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCultureConcat"));
    }
}
