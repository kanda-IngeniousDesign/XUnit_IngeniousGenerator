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

    private static Core.GenerationResult GenerateWith(string culture)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return TargetCompiler.Generate(Source, "Sample.Store");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
