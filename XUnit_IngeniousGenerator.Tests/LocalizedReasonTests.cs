using System.Globalization;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// テストしていない理由が、IDE の表示言語（CurrentUICulture）に合わせて英語・日本語で書かれることを確かめる
/// </summary>
public class LocalizedReasonTests
{
    private const string Source = """
        using System.IO;
        namespace Sample;
        public class Store
        {
            public int Count(string text) => text.Length;

            public string Load(string path) => File.ReadAllText(path);
        }
        """;

    [Fact]
    public void Generate_EnglishUiCulture_WritesReasonsInEnglish()
    {
        var result = GenerateWith("en-US");

        Assert.Contains(
            "[Fact(Skip = \"Not tested: 'File' depends on the external environment (files, databases, network), so it is not covered by unit tests (check it in UI tests)\")]",
            result.Code);
    }

    [Fact]
    public void Generate_JapaneseUiCulture_WritesReasonsInJapanese()
    {
        var result = GenerateWith("ja-JP");

        Assert.Contains(
            "[Fact(Skip = \"未テスト: 'File' は外部環境（ファイル・DB・通信など）に依存するため、単体テストでは担保しません（UIオーケストレーター側で確認）\")]",
            result.Code);
    }

    [Fact]
    public void Generate_OtherUiCulture_FallsBackToEnglish()
    {
        var result = GenerateWith("fr-FR");

        Assert.Contains("Skip = \"Not tested: ", result.Code);
    }

    [Fact]
    public void Resolve_VisualStudioLanguage_WinsOverThreadCulture()
    {
        // Visual Studio が Fix を計算するスレッドは英語でも、VS が日本語なら VSLANG=1041 で日本語にする
        var culture = Core.UiLanguage.Resolve(name => name == "VSLANG" ? "1041" : null, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("ja-JP", culture.Name);
    }

    [Fact]
    public void Resolve_NoVisualStudioLanguage_UsesThreadCulture()
    {
        var culture = Core.UiLanguage.Resolve(_ => null, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("en-US", culture.Name);
    }

    [Fact]
    public void Resolve_EnglishThreadOnJapaneseOs_UsesOsLanguage()
    {
        // VS の別のプロセスのスレッドが英語でも、日本語の Windows なら日本語にする
        var culture = Core.UiLanguage.Resolve(_ => null, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("ja-JP"));

        Assert.Equal("ja-JP", culture.Name);
    }

    [Fact]
    public void Use_Setting_WinsOverEverything()
    {
        using (Core.UiLanguage.Use("en"))
        {
            Assert.Equal("en", Core.UiLanguage.Current().Name);
        }

        Assert.Equal("ja-JP", Core.UiLanguage.Current().Name);
    }

    [Fact]
    public void Resolve_InvalidVisualStudioLanguage_UsesThreadCulture()
    {
        var culture = Core.UiLanguage.Resolve(name => name == "VSLANG" ? "abc" : null, CultureInfo.GetCultureInfo("ja-JP"), CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("ja-JP", culture.Name);
    }

    private static Core.GenerationResult GenerateWith(string culture)
    {
        // csproj の IngeniousLanguage で言語を指定した場合と同じ
        using (Core.UiLanguage.Use(culture))
        {
            return TargetCompiler.Generate(Source, "Sample.Store");
        }
    }
}
