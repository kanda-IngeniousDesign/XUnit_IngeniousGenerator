using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// メンバーごとのテストの状態（テスト済み・生成できる・生成できない・確かめる処理なし）の判定を確かめる
/// </summary>
public class TestCoverageInspectorTests
{
    private const string Source = """
        using System;
        using System.IO;
        namespace Sample;
        public class Service
        {
            private readonly string _name;
            public Service(string name) { _name = name; }
            public int Twice(int x) => x * 2;
            public int Add(int a, int b) => a + b;
            public string Load(string path) => File.ReadAllText(path);
        }
        """;

    [Fact]
    public void Inspect_WithoutTestFile_ClassifiesEachMember()
    {
        var statuses = Inspect([]);

        Assert.Equal(CoverageStatus.NothingToTest, statuses["Service"].Status);
        Assert.Equal(CoverageStatus.Generatable, statuses["Twice"].Status);
        Assert.Equal(CoverageStatus.Generatable, statuses["Add"].Status);
        var load = statuses["Load"];
        Assert.Equal(CoverageStatus.NotGeneratable, load.Status);
        Assert.Contains(load.Reasons, r => r.Contains("ファイル", StringComparison.Ordinal));
        Assert.DoesNotContain(load.Reasons, r => r.StartsWith("Load(", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_ExistingTestsByPrefix_AreCovered()
    {
        const string testFile = """
            public class ServiceTests
            {
                [Fact] public void Twice_BoundaryValues_ReturnsExpected() { }
                [Fact] public void TwiceAll_Something_Works() { }
            }
            """;

        var statuses = Inspect(TestCoverageInspector.CollectTestNames(testFile, "ServiceTests"));

        Assert.Equal(CoverageStatus.Covered, statuses["Twice"].Status);
        Assert.Equal(CoverageStatus.Generatable, statuses["Add"].Status);
    }

    /// <summary>
    /// オーバーロードは引数の型を含むテスト名で関数ごとに判定し、入れ子のクラスの関数も一覧の対象にすることを確認する
    /// </summary>
    [Fact]
    public void Inspect_OverloadsAndNestedClass_JudgedPerMember()
    {
        const string source = """
            namespace Sample;
            public class Pricing
            {
                public int Add(int a) => a + 1;
                public decimal Add(decimal a) => a + 1m;

                public class Rule
                {
                    public int Apply(int a) => a * 2;
                }
            }
            """;
        const string testFile = """
            public class PricingTests
            {
                [Fact] public void Add_Int_BoundaryValues_ReturnsExpected() { }
            }
            """;

        var compilation = TargetCompiler.Compile(source);
        var type = compilation.GetTypeByMetadataName("Sample.Pricing")!;
        var coverages = TestCoverageInspector.Inspect(
            type, compilation, new GenerationOptions(useMoq: true, useVerify: true), TestCoverageInspector.CollectTestNames(testFile, "PricingTests"));

        Assert.Equal(CoverageStatus.Covered, coverages.Single(c => c.Member.Name == "Add" && c.Member.Parameters[0].Type.Name == "Int32").Status);
        Assert.Equal(CoverageStatus.Generatable, coverages.Single(c => c.Member.Name == "Add" && c.Member.Parameters[0].Type.Name == "Decimal").Status);
        var apply = coverages.Single(c => c.Member.Name == "Apply");
        Assert.Equal(CoverageStatus.NotGeneratable, apply.Status);
        Assert.Equal(["入れ子のクラスは自動生成の対象外です"], apply.Reasons);
    }

    [Fact]
    public void FromProjectFile_DetectsMoqAndVerify()
    {
        var options = GenerationOptions.FromProjectFile("""
            <ItemGroup>
              <PackageReference Include="Moq" Version="4.21.0" />
              <PackageReference Include="xunit" Version="2.9.3" />
            </ItemGroup>
            """);

        Assert.True(options.UseMoq);
        Assert.False(options.UseVerify);
    }

    private static Dictionary<string, MemberCoverage> Inspect(IReadOnlyCollection<string> existingTestNames)
    {
        var compilation = TargetCompiler.Compile(Source);
        var type = compilation.GetTypeByMetadataName("Sample.Service")!;
        return TestCoverageInspector.Inspect(type, compilation, new GenerationOptions(useMoq: true, useVerify: true), existingTestNames)
            .ToDictionary(c => c.Member.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor ? "Service" : c.Member.Name);
    }
}
