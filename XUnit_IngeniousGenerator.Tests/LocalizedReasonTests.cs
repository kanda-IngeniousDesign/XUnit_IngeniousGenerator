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
    public void RuleTitle_IdePassesEnglish_UsesUiLanguageInstead()
    {
        // VS は警告のタイトルを、VS が決めた言語（英語になっていた）で取り出すため、渡された言語は使わない
        using var language = XUnit_IngeniousGenerator.Core.UiLanguage.Use("ja");

        Assert.Equal("テストコードの生成によるテスト漏れの防止", MissingTest.ClassRule.Title.ToString(new CultureInfo("en-US")));
        Assert.Equal("自動生成できないテストの明示", MissingTest.NotGeneratableRule.Title.ToString(new CultureInfo("en-US")));
    }

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
    public void Detect_VisualStudioJapanese_IsJapanese()
    {
        // VS の解析用のプロセスのスレッドが英語でも、VS が日本語（VSLANG=1041）なら日本語
        Assert.True(Core.UiLanguage.Detect("1041", CultureInfo.GetCultureInfo("en-US"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Detect_AnyCultureJapanese_IsJapanese()
    {
        // 表示言語が英語・言語なしでも、地域や OS の表示言語が日本語なら日本語
        Assert.True(Core.UiLanguage.Detect(null, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("ja-JP"), CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Detect_NothingJapanese_IsEnglish()
    {
        Assert.False(Core.UiLanguage.Detect("1033", CultureInfo.GetCultureInfo("en-US"), CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("fr-FR")));
        Assert.False(Core.UiLanguage.Detect("abc", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Use_Setting_WinsOverDetection()
    {
        // テストのプロセスは日本語（TestCulture）だが、csproj で en を指定すれば英語
        using (Core.UiLanguage.Use("en"))
        {
            Assert.False(Core.UiLanguage.IsJapanese());
            Assert.Equal("Fix : Generate test code in P", Core.Messages.Get("Fix_GenerateTests", "P"));
        }

        Assert.True(Core.UiLanguage.IsJapanese());
        Assert.Equal("Fix : テストコードを生成する（P）", Core.Messages.Get("Fix_GenerateTests", "P"));
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
