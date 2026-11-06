using System.Globalization;
using System.Runtime.CompilerServices;

namespace XUnit_IngeniousGenerator.Tests.Support;

/// <summary>
/// テストしていない理由などの文言は IDE の表示言語で切り替わるため、テストは日本語の表示言語で動かす
/// （CI の英語環境でも、期待値の日本語の文言と一致させるため）
/// </summary>
internal static class TestCulture
{
    /// <summary>
    /// テストのアセンブリを読み込んだときに、すべてのスレッドの表示言語を日本語にする
    /// </summary>
    [ModuleInitializer]
    internal static void UseJapanese()
    {
        var japanese = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = japanese;
        CultureInfo.CurrentUICulture = japanese;
    }
}
