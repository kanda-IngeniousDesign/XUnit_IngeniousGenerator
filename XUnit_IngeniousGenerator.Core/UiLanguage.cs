using System.Globalization;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// テストしていない理由・Fix のタイトルを書く言語（IDE の表示言語）を決める。
/// Visual Studio は Fix やテストの生成を別のプロセスで計算することがあり、そのスレッドの表示言語は VS の表示言語と一致しない
/// （日本語の VS でも英語になる。2026-11-19 に会社の VS で、VSLANG も渡っていないことを確認）。そのため、次の順に決める。
/// 1. csproj の IngeniousLanguage（ja・en など）で指定された言語
/// 2. VSLANG（VS・dotnet が起動するプロセスに渡す表示言語の LCID。日本語は 1041）
/// 3. スレッドの表示言語。ただし英語（または言語なし）で、Windows などの OS の表示言語が英語でなければ、OS の表示言語
/// </summary>
public static class UiLanguage
{
    /// <summary>csproj の IngeniousLanguage を、解析に渡すときの名前</summary>
    public const string SettingKey = "build_property.IngeniousLanguage";

    private static readonly AsyncLocal<CultureInfo?> Configured = new();

    /// <summary>
    /// 今の表示言語を返す
    /// </summary>
    /// <returns>表示言語</returns>
    public static CultureInfo Current()
    {
        return Configured.Value ?? Resolve(Environment.GetEnvironmentVariable, CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture);
    }

    /// <summary>
    /// csproj で指定された言語（IngeniousLanguage）を、この呼び出しの間だけ使う（指定がなければ自動で決める）
    /// </summary>
    /// <param name="setting">指定された言語（ja・en など。なければnull）</param>
    /// <returns>使い終わったら元に戻すためのもの</returns>
    public static IDisposable Use(string? setting)
    {
        var previous = Configured.Value;
        Configured.Value = string.IsNullOrWhiteSpace(setting) ? previous : TryCreate(() => new CultureInfo(setting!.Trim())) ?? previous;
        return new Scope(previous);
    }

    /// <summary>
    /// 表示言語を自動で決める（VSLANG、スレッドの表示言語、OS の表示言語の順に見る）
    /// </summary>
    /// <param name="environment">環境変数を読む処理</param>
    /// <param name="threadCulture">スレッドの表示言語</param>
    /// <param name="installedCulture">OS の表示言語</param>
    /// <returns>表示言語</returns>
    public static CultureInfo Resolve(Func<string, string?> environment, CultureInfo threadCulture, CultureInfo installedCulture)
    {
        if (int.TryParse(environment("VSLANG"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcid) && TryCreate(() => new CultureInfo(lcid)) is { } vs)
        {
            return vs;
        }

        // IDE の別のプロセスのスレッドは、表示言語が英語や言語なしになっていることがある。その場合は OS の表示言語を使う
        return IsEnglishOrInvariant(threadCulture) && !IsEnglishOrInvariant(installedCulture) ? installedCulture : threadCulture;
    }

    /// <summary>
    /// 英語、または言語なし（InvariantCulture）かを判定する
    /// </summary>
    /// <param name="culture">言語</param>
    /// <returns>英語・言語なしならtrue</returns>
    private static bool IsEnglishOrInvariant(CultureInfo culture)
    {
        return culture.TwoLetterISOLanguageName is "en" or "iv";
    }

    /// <summary>
    /// カルチャを作る（存在しない名前・LCID なら null）
    /// </summary>
    /// <param name="create">作る処理</param>
    /// <returns>カルチャ（作れなければnull）</returns>
    private static CultureInfo? TryCreate(Func<CultureInfo> create)
    {
        try
        {
            return create();
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 指定された言語を使い終わったら、元に戻す
    /// </summary>
    private sealed class Scope : IDisposable
    {
        private readonly CultureInfo? _previous;

        /// <summary>
        /// 元の言語を覚えておく
        /// </summary>
        /// <param name="previous">元の言語</param>
        public Scope(CultureInfo? previous)
        {
            _previous = previous;
        }

        /// <summary>
        /// 元の言語に戻す
        /// </summary>
        public void Dispose()
        {
            Configured.Value = _previous;
        }
    }
}
