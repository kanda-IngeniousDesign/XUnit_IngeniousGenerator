using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 文字列補間・string.Join などの文字列の組み立てと、列挙型の引数・戻り値の期待値を静的に計算し、
/// 生成したテストを実際にビルド・実行して成功することまで確かめる
/// </summary>
public class TextAndEnumGenerationTests
{
    private const string Source = """
        namespace Sample;
        public enum Rank { Gold, Silver }
        public class Greeter
        {
            public string Hello(string name) => $"{name} 様";
            public string Label(int count, string unit) => $"{count}{unit}";
            public string Tag(Rank rank) => $"[{rank}]";
            public string Csv(string a, string b) => string.Join(",", a, b);
            public string Code(int id) => "ID-" + id.ToString();
            public string Pad(string s) => $"{s,5}|";
            public string Fmt(string a, int n) => string.Format("{0}:{1}", a, n);
            public string OrEmpty(string s) => s.Length == 0 ? string.Empty : s;
            public decimal Discount(Rank rank, decimal price) => rank == Rank.Gold ? price * 0.9m : price;
            public Rank Next(Rank rank) => rank switch { Rank.Gold => Rank.Silver, _ => Rank.Gold };
            public string Money(decimal price) => $"{price:N0}円";
        }
        """;

    [Fact]
    public async Task Generate_StringBuildingAndEnums_TestsPassAgainstTarget()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Greeter");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, result.Code);

        Assert.Contains("[InlineData(\"a\", \"a 様\")]", result.Code);
        Assert.Contains("[InlineData(Rank.Silver, \"[Silver]\")]", result.Code);
        Assert.Contains("[InlineData(\"a\", \"    a|\")]", result.Code);
        Assert.Contains("[InlineData(Rank.Gold, Rank.Silver)]", result.Code);
        Assert.Equal(
            [
                "Hello_BoundaryValues_ReturnsExpected",
                "Label_BoundaryValues_ReturnsExpected",
                "Tag_BoundaryValues_ReturnsExpected",
                "Csv_BoundaryValues_ReturnsExpected",
                "Code_BoundaryValues_ReturnsExpected",
                "Pad_BoundaryValues_ReturnsExpected",
                "Fmt_BoundaryValues_ReturnsExpected",
                "OrEmpty_BoundaryValues_ReturnsExpected",
                "OrEmpty_SIsNull_ThrowsNullReferenceException",
                "Discount_BoundaryValues_ReturnsExpected",
                "Next_BoundaryValues_ReturnsExpected",
            ],
            passed);

        // 書式指定（{price:N0}）は、実行環境のカルチャで結果が変わるため計算しない
        Assert.Contains("public void Money_NotTested()", result.Code);
    }

    [Fact]
    public void Generate_NotComputable_WritesReasonAboveSnapshotTest()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Greeter", options: new GenerationOptions(useMoq: true, useVerify: true));

        Assert.Contains(
            "    // 戻り値を静的に計算できない理由: 文字列補間（カルチャに依存するため）\r\n    [Theory]\r\n    [MemberData(nameof(Money_SnapshotInputs))]",
            result.Code);
    }
}
