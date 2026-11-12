using System.Globalization;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// テストしていない理由・Fix のタイトルを書く言語（IDE の表示言語）を決める。
/// Visual Studio は Fix やテストの生成を別のプロセスで計算することがあり、そのスレッドの表示言語は VS の表示言語と一致しない
/// （日本語の VS でも英語になる）。そのため、VS が起動するプロセスに渡す VSLANG（表示言語の LCID。日本語は 1041）を優先する
/// </summary>
public static class UiLanguage
{
    /// <summary>
    /// 今の表示言語を返す
    /// </summary>
    /// <returns>表示言語</returns>
    public static CultureInfo Current()
    {
        return Resolve(Environment.GetEnvironmentVariable, CultureInfo.CurrentUICulture);
    }

    /// <summary>
    /// 表示言語を決める。VSLANG（Visual Studio の表示言語）があればそれを、なければスレッドの表示言語を使う
    /// （DOTNET_CLI_UI_LANGUAGE は dotnet コマンドの出力の言語で、IDE の表示言語とは限らないため使わない）
    /// </summary>
    /// <param name="environment">環境変数を読む処理</param>
    /// <param name="threadCulture">スレッドの表示言語</param>
    /// <returns>表示言語</returns>
    public static CultureInfo Resolve(Func<string, string?> environment, CultureInfo threadCulture)
    {
        if (int.TryParse(environment("VSLANG"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcid) && TryCreate(() => new CultureInfo(lcid)) is { } vs)
        {
            return vs;
        }

        return threadCulture;
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
}
