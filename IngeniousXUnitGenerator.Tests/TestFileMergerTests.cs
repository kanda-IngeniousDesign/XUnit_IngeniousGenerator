using IngeniousXUnitGenerator.Core;
using IngeniousXUnitGenerator.Tests.Support;

namespace IngeniousXUnitGenerator.Tests;

/// <summary>
/// 既存のテストファイルへのマージ（不足しているメンバーと using だけを追加し、既存の内容には触れない）を確かめる
/// </summary>
public class TestFileMergerTests
{
    private const string Source = """
        using System;
        namespace Sample;
        public class Calculator
        {
            public int Divide(int dividend, int divisor)
            {
                if (divisor == 0) throw new ArgumentOutOfRangeException(nameof(divisor));
                return dividend / divisor;
            }

            public int Twice(int value) => value * 2;
        }
        """;

    [Fact]
    public async Task Merge_BlockNamespaceWithLf_AddsMissingMembersWithMatchingIndentAndLineEndings()
    {
        // 人間が育てたテストファイル（ブロック形式の名前空間、LF、独自のテスト）
        const string existing = "using Xunit;\n\nnamespace Sample.Tests\n{\n    public class CalculatorTests\n    {\n        private readonly Calculator _sut = new Calculator();\n\n        [Fact]\n        public void Divide_DivisorIsZero_ThrowsArgumentOutOfRangeException()\n        {\n            // 人間が書き換えたテスト\n            Assert.Throws<ArgumentOutOfRangeException>(() => _sut.Divide(1, 0));\n        }\n\n        [Fact]\n        public void Divide_HumanWrittenCase()\n        {\n        }\n    }\n}\n";
        var generated = TargetCompiler.Generate(Source, "Sample.Calculator").Code;

        var merged = TestFileMerger.Merge(existing, generated);

        Assert.NotNull(merged);
        Assert.DoesNotContain("\r\n", merged);
        Assert.Contains("// 人間が書き換えたテスト", merged);
        Assert.Contains("public void Divide_HumanWrittenCase()", merged);
        Assert.Contains("\n        [Theory]\n        [InlineData(1, 1, 1)]", merged);
        Assert.Contains("\n        public void Twice_BoundaryValues_ReturnsExpected(int value, int expected)\n        {\n            Assert.Equal(expected, _sut.Twice(value));\n        }\n", merged);
        Assert.Contains("using System;\n", merged);
        Assert.Equal(1, merged!.Split("private readonly Calculator _sut").Length - 1);
        Assert.Equal(1, merged.Split("Divide_DivisorIsZero_ThrowsArgumentOutOfRangeException").Length - 1);

        // マージ後のファイルがコンパイルでき、生成したテストも人間のテストも成功すること
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, merged);
        Assert.Contains("Twice_BoundaryValues_ReturnsExpected", passed);
        Assert.Contains("Divide_HumanWrittenCase", passed);
    }

    [Fact]
    public void Merge_NothingNew_ReturnsNull()
    {
        var generated = TargetCompiler.Generate(Source, "Sample.Calculator").Code;

        Assert.Null(TestFileMerger.Merge(generated, generated));
    }

    [Fact]
    public void Merge_NoMatchingTestClass_ReturnsNull()
    {
        var generated = TargetCompiler.Generate(Source, "Sample.Calculator").Code;

        Assert.Null(TestFileMerger.Merge("// 空のファイル\n", generated));
        Assert.Null(TestFileMerger.Merge("public class OtherTests { }\n", generated));
    }

    [Fact]
    public async Task Generate_OnlyMember_GeneratesTestsForThatMethodOnly()
    {
        var compilation = TargetCompiler.Compile(Source);
        var targetType = compilation.GetTypeByMetadataName("Sample.Calculator")!;
        var twice = targetType.GetMembers("Twice").OfType<Microsoft.CodeAnalysis.IMethodSymbol>().Single();

        var result = TestClassGenerator.Generate(targetType, compilation, "Sample.Tests", new GenerationOptions(false, false, twice));
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, result.Code);

        Assert.Equal(["Twice_BoundaryValues_ReturnsExpected"], passed);
        Assert.DoesNotContain("Divide", result.Code);
    }
}
