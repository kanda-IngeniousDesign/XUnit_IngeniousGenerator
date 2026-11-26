using System.Globalization;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 文言（警告・Fix のタイトル・テストしていない理由・テストのコメント）を、UiLanguage で決めた言語で取り出す。
/// 文言はコードの中（MessageTexts）に持つ
/// </summary>
public static class Messages
{
    /// <summary>
    /// 文言を取り出し、引数を埋め込む
    /// </summary>
    /// <param name="name">文言の名前</param>
    /// <param name="arguments">埋め込む値</param>
    /// <returns>文言</returns>
    public static string Get(string name, params object?[] arguments)
    {
        if (!MessageTexts.All.TryGetValue(name, out var texts)) throw new InvalidOperationException($"Message '{name}' is not defined.");

        var format = UiLanguage.IsJapanese() ? texts.Ja : texts.En;
        return arguments.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, arguments);
    }
}
