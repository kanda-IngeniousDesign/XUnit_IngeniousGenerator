using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// ガードより後ろにある throw に到達する入力を静的な評価で見つけ、例外テストを生成して実際に実行する
/// </summary>
public class LaterThrowGenerationTests
{
    [Fact]
    public async Task Generate_ThrowAfterOtherStatements_FindsReachingInputAndChecksParamName()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Formatter
            {
                public string Format(string label, int digits)
                {
                    if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required", nameof(label));
                    var text = label.Trim();
                    if (digits < 0) throw new ArgumentOutOfRangeException(nameof(digits));
                    if (text.Length == 0) throw new InvalidOperationException("blank label");
                    return text.PadLeft(digits, '0');
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Formatter");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("Format_DigitsIsMinus1_ThrowsArgumentOutOfRangeException", passed);
        Assert.Contains("Format_LabelIsWhiteSpace_ThrowsInvalidOperationException", passed);
        Assert.Contains("Assert.Equal(\"digits\", exception.ParamName);", result.Code);
        Assert.Empty(result.ManualItems);
    }

    [Fact]
    public void Generate_ThrowNotReachableByStaticEvaluation_StaysManual()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Parser
            {
                public int Parse(string text)
                {
                    var value = int.Parse(text);
                    if (value < 0) throw new FormatException("negative");
                    return value;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Parser");

        Assert.DoesNotContain("_Throws", result.Code);
        Assert.Contains("Parse(string): ガードより後ろにある throw 1 件は、そこに到達する入力を求められないため例外テストの対象外です", result.ManualItems);
    }
}
