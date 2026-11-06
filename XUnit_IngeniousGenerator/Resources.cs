using System.Globalization;
using System.Resources;

namespace XUnit_IngeniousGenerator;

// 警告のタイトル・メッセージと Fix のタイトル（Resources.resx と、日本語の Resources.ja.resx）
internal static class Resources
{
    /// <summary>リソースの読み込み</summary>
    public static ResourceManager ResourceManager { get; } = new("XUnit_IngeniousGenerator.Resources", typeof(Resources).Assembly);

    /// <summary>
    /// IDE の表示言語の文言を取り出し、引数を埋め込む（Fix のタイトルなど）
    /// </summary>
    /// <param name="name">文言の名前</param>
    /// <param name="arguments">埋め込む値</param>
    /// <returns>文言</returns>
    public static string Get(string name, params object[] arguments)
    {
        // Fix は IDE の中だけで動くため、IDE の表示言語（スレッドの表示言語）の文言を使う
        var format = ResourceManager.GetString(name)
            ?? throw new InvalidOperationException($"Resource '{name}' is not defined.");
        return arguments.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, arguments);
    }
}
