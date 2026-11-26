using System.Globalization;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 文言（警告・Fix のタイトル・テストしていない理由・テストのコメント）を日本語で書くか、英語で書くかを決める。
/// 1. csproj の IngeniousLanguage（ja なら日本語、それ以外の値なら英語）
/// 2. 指定がなければ、VS の表示言語（VSLANG）・スレッドの表示言語と地域・OS の表示言語のどれか1つでも日本語なら日本語
/// （VS の解析用のプロセスでは、一部が英語や言語なしになっていることがあるため、どれか1つで判断する）
/// </summary>
public static class UiLanguage
{
    /// <summary>csproj の IngeniousLanguage を読むキー（build/XUnit_IngeniousGenerator.targets で解析に渡す）</summary>
    public const string SettingKey = "build_property.IngeniousLanguage";

    private const int JapaneseLcid = 1041;

    // csproj の設定（Fix の計算の途中でも引き継ぐため AsyncLocal。null なら自動で決める）
    private static readonly AsyncLocal<bool?> Configured = new();

    /// <summary>
    /// 日本語で書くかを返す
    /// </summary>
    /// <returns>日本語ならtrue</returns>
    public static bool IsJapanese()
    {
        return Configured.Value ?? Detect(
            Environment.GetEnvironmentVariable("VSLANG"), CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture, CultureInfo.InstalledUICulture);
    }

    /// <summary>
    /// csproj の設定を、この範囲で使う（空なら自動で決める）
    /// </summary>
    /// <param name="setting">IngeniousLanguage の値（ja・en など）</param>
    /// <returns>範囲を抜けるときに元に戻すもの</returns>
    public static IDisposable Use(string? setting)
    {
        var previous = Configured.Value;
        if (!string.IsNullOrWhiteSpace(setting)) Configured.Value = setting!.Trim().StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        return new Scope(previous);
    }

    /// <summary>
    /// 自動で決める（VS の表示言語・スレッドの表示言語と地域・OS の表示言語のどれか1つでも日本語なら日本語）
    /// </summary>
    /// <param name="vsLanguage">環境変数 VSLANG の値（VS の表示言語の番号。日本語は 1041）</param>
    /// <param name="cultures">スレッドの表示言語・地域、OS の表示言語</param>
    /// <returns>日本語ならtrue</returns>
    public static bool Detect(string? vsLanguage, params CultureInfo[] cultures)
    {
        if (int.TryParse(vsLanguage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcid) && lcid == JapaneseLcid) return true;
        return cultures.Any(c => c.TwoLetterISOLanguageName == "ja");
    }

    /// <summary>
    /// 範囲を抜けるときに、元の設定に戻す
    /// </summary>
    private sealed class Scope : IDisposable
    {
        private readonly bool? _previous;

        /// <summary>
        /// 元の設定を覚えておく
        /// </summary>
        /// <param name="previous">元の設定</param>
        public Scope(bool? previous)
        {
            _previous = previous;
        }

        /// <summary>
        /// 元の設定に戻す
        /// </summary>
        public void Dispose()
        {
            Configured.Value = _previous;
        }
    }
}
