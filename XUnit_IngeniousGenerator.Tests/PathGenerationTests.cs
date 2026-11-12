using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// Path の文字列の操作（Combine・GetFileName など）の期待値を、生成した OS の区切り文字で計算し、
/// 生成したテストを実際にビルド・実行して成功することまで確かめる
/// </summary>
public class PathGenerationTests
{
    [Fact]
    public async Task Generate_PathOperations_TestsPassOnTheGeneratingOs()
    {
        const string source = """
            using System.IO;
            namespace Sample;
            public class Files
            {
                private readonly string _root = "data";

                public string Locate(string name) => Path.Combine(_root, name + ".csv");

                public string Name(string path) => Path.GetFileNameWithoutExtension(path);

                public string Rename(string path) => Path.ChangeExtension(path, ".bak");

                public bool IsCsv(string path) => Path.GetExtension(path) == ".csv";
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Files");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        var expected = Path.Combine("data", "a.csv").Replace("\\", "\\\\");
        Assert.Contains($"[InlineData(\"a\", \"{expected}\")]", result.Code);
        Assert.Equal(
            [
                "Locate_BoundaryValues_ReturnsExpected",
                "Name_BoundaryValues_ReturnsExpected",
                "Rename_BoundaryValues_ReturnsExpected",
                "IsCsv_BoundaryValues_ReturnsExpected",
            ],
            passed.Where(n => !n.Contains("Throws")));
    }
}
