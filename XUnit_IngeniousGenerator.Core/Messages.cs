using System.Globalization;
using System.Resources;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// テストしていない理由・レポートの文言（英語の Messages.resx と、日本語の Messages.ja.resx）。
/// 警告のメッセージと同じく、IDE の表示言語で選ぶ（決め方は UiLanguage）
/// </summary>
internal static class Messages
{
    private static readonly ResourceManager ResourceManager = new("XUnit_IngeniousGenerator.Core.Messages", typeof(Messages).Assembly);

    /// <summary>
    /// 文言を取り出し、引数を埋め込む
    /// </summary>
    /// <param name="name">文言の名前</param>
    /// <param name="arguments">埋め込む値</param>
    /// <returns>文言</returns>
    public static string Get(string name, params object?[] arguments)
    {
        var format = ResourceManager.GetString(name, UiLanguage.Current())
            ?? throw new InvalidOperationException($"Message '{name}' is not defined.");
        return arguments.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, arguments);
    }
}
