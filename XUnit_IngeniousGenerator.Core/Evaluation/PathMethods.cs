using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// System.IO.Path の文字列の操作（Combine・GetFileName など）を評価する。ファイルには触れない。
/// 区切り文字は、生成した OS（解析を動かしている環境）のものになる（Windows は \、Mac・Linux は /。2026-11-12 に利用者が決定）
/// </summary>
internal static class PathMethods
{
    private const string PathTypeName = "System.IO.Path";

    /// <summary>
    /// Path の静的メソッドを評価する
    /// </summary>
    /// <param name="method">メソッド</param>
    /// <param name="arguments">引数（評価済み）</param>
    /// <param name="site">呼び出し式</param>
    /// <param name="value">戻り値</param>
    /// <returns>Path のメソッドならtrue</returns>
    public static bool TryInvoke(IMethodSymbol method, IReadOnlyList<object?> arguments, InvocationExpressionSyntax site, out object? value)
    {
        value = null;
        if (method.ContainingType.ToDisplayString() != PathTypeName) return false;
        if (arguments.Any(a => a is not (null or string))) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", site.Expression));

        var texts = arguments.Cast<string?>().ToList();
        try
        {
            value = (method.Name, texts.Count) switch
            {
                ("Combine", >= 2) => Combine(texts, site),
                ("GetFileName", 1) => Path.GetFileName(texts[0]),
                ("GetFileNameWithoutExtension", 1) => Path.GetFileNameWithoutExtension(texts[0]),
                ("GetExtension", 1) => Path.GetExtension(texts[0]),
                ("GetDirectoryName", 1) => Path.GetDirectoryName(texts[0]),
                ("ChangeExtension", 2) => Path.ChangeExtension(texts[0], texts[1]),
                ("HasExtension", 1) => Path.HasExtension(texts[0]),
                _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", site.Expression)),
            };
            return true;
        }
        catch (ArgumentException)
        {
            // 使えない文字の扱いなど、.NET の版で結果が変わりうるものは計算しない
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", site.Expression));
        }
    }

    /// <summary>
    /// Path の静的フィールド（DirectorySeparatorChar など）を読む
    /// </summary>
    /// <param name="symbol">フィールド</param>
    /// <param name="value">値</param>
    /// <returns>Path のフィールドならtrue</returns>
    public static bool TryReadField(ISymbol? symbol, out object? value)
    {
        value = (symbol as IFieldSymbol)?.ContainingType.ToDisplayString() != PathTypeName ? null : symbol!.Name switch
        {
            nameof(Path.DirectorySeparatorChar) => Path.DirectorySeparatorChar,
            nameof(Path.AltDirectorySeparatorChar) => Path.AltDirectorySeparatorChar,
            _ => null,
        };
        return value is not null;
    }

    /// <summary>
    /// Path.Combine を評価する（null の引数は、実行時と同じく ArgumentNullException）
    /// </summary>
    /// <param name="parts">つなぐパス</param>
    /// <param name="site">呼び出し式</param>
    /// <returns>つないだパス</returns>
    private static string Combine(List<string?> parts, InvocationExpressionSyntax site)
    {
        if (parts.Any(p => p is null)) throw new GuestThrowException(null, "System.ArgumentNullException", null, site);
        return Path.Combine(parts.Cast<string>().ToArray());
    }
}
