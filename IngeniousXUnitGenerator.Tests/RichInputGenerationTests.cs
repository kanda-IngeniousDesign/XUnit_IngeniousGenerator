using IngeniousXUnitGenerator.Core;
using IngeniousXUnitGenerator.Tests.Support;

namespace IngeniousXUnitGenerator.Tests;

/// <summary>
/// 列挙型（全メンバー）・コレクション（1件・空・null）・文字列（空白・null）の入力を生成し、実際に実行して確かめる
/// </summary>
public sealed class RichInputGenerationTests : IDisposable
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        namespace Sample;
        public enum Unit { Meter, Feet, Inch }
        public class Recorder
        {
            private readonly List<string> _log = new();

            public void Record(Unit unit, int[] values, List<string>? tags, string? note)
            {
                _log.Add(unit + ":" + values.Length + ":" + (tags?.Count ?? -1) + ":" + (note ?? "(null)"));
            }

            public string Describe(Unit unit, IEnumerable<int> values) => unit + "=" + values.Sum();
        }
        """;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ingenious-xunit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact]
    public async Task Generate_NoThrowWithEnumsCollectionsAndNullableStrings_UsesAllVariations()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Recorder");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, result.Code);

        Assert.Contains("Record_BoundaryValues_DoesNotThrow", passed);
        Assert.Contains("public static TheoryData<Unit, int[], List<string>?, string?> Record_NoThrowInputs", result.Code);
        Assert.Contains("{ Unit.Meter, new int[] { 1 }, new List<string> { \"a\" }, \"a\" }", result.Code);
        Assert.Contains("{ Unit.Feet,", result.Code);
        Assert.Contains("{ Unit.Inch,", result.Code);
        Assert.Contains("Array.Empty<int>()", result.Code);
        Assert.Contains("new List<string>()", result.Code);
        Assert.Contains("\" \" }", result.Code);
        Assert.Contains("null }", result.Code);

        // null 許容でない int[] には null を渡さない
        Assert.DoesNotContain("{ Unit.Meter, null,", result.Code);
    }

    [Fact]
    public async Task Generate_SnapshotWithEnumAndCollection_VariesEnumButKeepsCollectionFixed()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Recorder", options: new GenerationOptions(useMoq: true, useVerify: true));
        var run = await DotnetTestRunner.RunAsync(_workDirectory, Source, result.Code, "RecorderTests.cs");

        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("public Task Describe_BoundaryValues_MatchesSnapshot(Unit unit, IEnumerable<int> values)", result.Code);
        Assert.Contains("{ Unit.Inch, new int[] { 1 } }", result.Code);
        Assert.DoesNotContain("Array.Empty<int>() }", result.Code.Split("Describe_SnapshotInputs")[1]);
        Assert.Equal(3, Directory.GetFiles(_workDirectory, "RecorderTests.Describe_BoundaryValues_MatchesSnapshot_*.verified.txt").Length);
    }
}
