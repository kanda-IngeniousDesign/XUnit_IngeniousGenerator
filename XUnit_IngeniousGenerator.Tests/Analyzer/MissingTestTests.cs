using XUnit_IngeniousGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;


namespace XUnit_IngeniousGenerator.Tests.Analyzer;

using Verify = CSharpAnalyzerVerifier<MissingTest, DefaultVerifier>;
using CodeFixVerify = CSharpCodeFixVerifier<MissingTest, MissingTestFix, DefaultVerifier>;

/// <summary>
/// TEST001（MissingTest）の検知と、Ignore コメントを挿入する Fix を検証するテスト
/// </summary>
public class MissingTestTests
{
    private const string TestFilePath = "/0/Product.Tests/CalculatorTests.cs";

    // 既定で有効だが、テストでは globalconfig で明示する。テストプロジェクトのファイルには targets と同じメタデータを付ける
    private const string EnableRule = """
        is_global = true
        dotnet_diagnostic.TEST001.severity = warning
        dotnet_diagnostic.TEST002.severity = warning
        """;

    private const string EnableRuleWithTestFile = EnableRule + """


        [/0/Product.Tests/CalculatorTests.cs]
        build_metadata.AdditionalFiles.IngeniousTestFile = true
        """;

    /// <summary>
    /// public なメソッドを持つクラスにテストファイルがなければ、クラス名に診断が出ることを確認する
    /// </summary>
    [Fact]
    public async Task ClassWithoutTestFile_ReportsDiagnostic()
    {
        var test = CreateTest("""
            public class {|#0:Calculator|}
            {
                public int Add(int a, int b) => a + b;
            }
            """, EnableRule);
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(0).WithArguments("Calculator"));

        await test.RunAsync();
    }

    /// <summary>
    /// テストプロジェクトに XxxTests.cs があれば、診断が出ないことを確認する
    /// </summary>
    [Fact]
    public async Task ClassWithTestFile_DoesNotReportDiagnostic()
    {
        var test = CreateTest("""
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """, EnableRuleWithTestFile);
        test.TestState.AdditionalFiles.Add((TestFilePath, "public class CalculatorTests { public void Add_BoundaryValues_ReturnsExpected() { } }"));

        await test.RunAsync();
    }

    /// <summary>
    /// テストファイルはあるが、テストのない関数があれば、その関数名に診断が出ることを確認する
    /// （関数のテストは「関数名_」で始まるテストメソッドで判定する）
    /// </summary>
    [Fact]
    public async Task MemberWithoutTests_ReportsDiagnosticOnMemberName()
    {
        var test = CreateTest("""
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public int {|#0:AddAll|}(int a, int b, int c) => a + b + c;
            }
            """, EnableRuleWithTestFile);
        test.TestState.AdditionalFiles.Add((TestFilePath, "public class CalculatorTests { public void Add_BoundaryValues_ReturnsExpected() { } }"));
        test.ExpectedDiagnostics.Add(Verify.Diagnostic(MissingTest.MemberRule).WithLocation(0).WithArguments("AddAll", "CalculatorTests"));

        await test.RunAsync();
    }

    /// <summary>
    /// テストのないコンストラクタ・プロパティの setter にも、その名前に診断が出ることを確認する
    /// </summary>
    [Fact]
    public async Task ConstructorAndPropertySetterWithoutTests_ReportDiagnosticOnTheirNames()
    {
        var test = CreateTest("""
            public class Calculator
            {
                private int _limit;

                public {|#0:Calculator|}(int limit)
                {
                    if (limit < 0) throw new System.ArgumentOutOfRangeException(nameof(limit));
                    _limit = limit;
                }

                public int {|#1:Limit|}
                {
                    get => _limit;
                    set
                    {
                        if (value < 0) throw new System.ArgumentOutOfRangeException(nameof(value));
                        _limit = value;
                    }
                }

                public int Add(int a, int b) => a + b;
            }
            """, EnableRuleWithTestFile);
        test.TestState.AdditionalFiles.Add((TestFilePath, "public class CalculatorTests { public void Add_BoundaryValues_ReturnsExpected() { } }"));
        test.ExpectedDiagnostics.Add(Verify.Diagnostic(MissingTest.MemberRule).WithLocation(0).WithArguments("Calculator", "CalculatorTests"));
        test.ExpectedDiagnostics.Add(Verify.Diagnostic(MissingTest.MemberRule).WithLocation(1).WithArguments("Limit", "CalculatorTests"));

        await test.RunAsync();
    }

    /// <summary>
    /// 字下げのない宣言（直前が説明コメントだけ）でも、Ignore の Fix がコメントの下に「// Ignore TEST001」を挿入することを確認する
    /// </summary>
    [Fact]
    public async Task IgnoreFix_DeclarationWithoutIndent_InsertsCommentBelowExistingComment()
    {
        var test = new CSharpCodeFixTest<MissingTest, MissingTestFix, DefaultVerifier>
        {
            TestCode = """
                // 計算
                public class {|#0:Calculator|}
                {
                    public int Add(int a, int b) => a + b;
                }
                """,
            FixedCode = """
                // 計算
                // Ignore TEST001
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", EnableRule));
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(0).WithArguments("Calculator"));

        await test.RunAsync();
    }

    /// <summary>
    /// 自動生成できない関数には、理由とともに TEST002 が出て、TEST001 は出ないことを確認する
    /// （Ignore TEST002 を付けた関数には出ない）
    /// </summary>
    [Fact]
    public async Task NotGeneratableMember_ReportsTest002WithReason()
    {
        var test = CreateTest("""
            using System.IO;
            public class Loader
            {
                public string {|#0:Load|}(string path) => File.ReadAllText(path);

                // Ignore TEST002
                public string LoadAgain(string path) => File.ReadAllText(path);
            }
            """, EnableRule);
        test.ExpectedDiagnostics.Add(Verify.Diagnostic(MissingTest.NotGeneratableRule).WithLocation(0)
            .WithArguments("Load", "'File' は外部環境（ファイル・DB・通信など）に依存するため、単体テストでは担保しません（UIオーケストレーター側で確認）"));

        await test.RunAsync();
    }

    /// <summary>
    /// TEST001・TEST002 は既定で有効で、設定で重大度を none にした場合だけ無効と判定することを確認する
    /// （無効なら判定の処理自体を行わない）
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(ReportDiagnostic.Default, true)]
    [InlineData(ReportDiagnostic.Suppress, false)]
    [InlineData(ReportDiagnostic.Warn, true)]
    [InlineData(ReportDiagnostic.Info, true)]
    public void IsEnabled_FollowsConfiguredSeverity(ReportDiagnostic? severity, bool expected)
    {
        var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (severity is { } value) options = options.WithSpecificDiagnosticOptions([new(MissingTest.DiagnosticId, value)]);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class C { }");

        Assert.Equal(expected, MissingTest.IsEnabled(options, tree, MissingTest.DiagnosticId, CancellationToken.None));
    }

    /// <summary>
    /// 同名のファイルでも、テストプロジェクトのファイル（メタデータ付き）でなければテストとみなさないことを確認する
    /// </summary>
    [Fact]
    public async Task SameFileNameOutsideTestProject_ReportsDiagnostic()
    {
        var test = CreateTest("""
            public class {|#0:Calculator|}
            {
                public int Add(int a, int b) => a + b;
            }
            """, EnableRule);
        test.TestState.AdditionalFiles.Add((TestFilePath, string.Empty));
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(0).WithArguments("Calculator"));

        await test.RunAsync();
    }

    /// <summary>
    /// パッケージを入れれば使えるよう、既定で有効（.editorconfig で none にすれば止められる）であることを確認する
    /// </summary>
    [Fact]
    public void Rule_IsEnabledByDefault()
    {
        foreach (var descriptor in new MissingTest().SupportedDiagnostics)
        {
            Assert.True(descriptor.IsEnabledByDefault);
            Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
        }
    }

    /// <summary>
    /// テストを求めないクラス（プロパティだけ・抽象・入れ子・Ignore コメント付き・引数なしコンストラクタだけ）には診断が出ないことを確認する
    /// </summary>
    [Fact]
    public async Task ClassesWithoutTestableMembersOrIgnored_DoNotReportDiagnostic()
    {
        var test = CreateTest("""
            public class Dto
            {
                public Dto() { }

                public int Id { get; set; }
            }

            public abstract class Base
            {
                public abstract int Run();
            }

            public class Outer
            {
                internal Outer() { }

                public class Inner
                {
                    public void Run() { }
                }
            }

            // Ignore TEST001
            public class Legacy
            {
                public void Run() { }
            }
            """, EnableRule);

        await test.RunAsync();
    }

    /// <summary>
    /// static クラスや、ガードのあるコンストラクタだけのクラスは対象になり、
    /// 確かめる処理のないコンストラクタだけのクラスは対象にならないことを確認する
    /// </summary>
    [Fact]
    public async Task StaticClassAndConstructorWithGuard_ReportDiagnostics()
    {
        var test = CreateTest("""
            public static class {|#0:MathHelper|}
            {
                public static int Twice(int value) => value * 2;
            }

            public class {|#1:Greeter|}
            {
                private readonly string _prefix;

                public Greeter(string prefix)
                {
                    if (prefix is null) throw new System.ArgumentNullException(nameof(prefix));
                    _prefix = prefix;
                }
            }

            public class Holder
            {
                private readonly string _value;

                public Holder(string value) { _value = value; }
            }
            """, EnableRule);
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(0).WithArguments("MathHelper"));
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(1).WithArguments("Greeter"));

        await test.RunAsync();
    }

    /// <summary>
    /// partial クラスは、最初の宣言にだけ診断が出ることを確認する
    /// </summary>
    [Fact]
    public async Task PartialClass_ReportsOnlyOnFirstDeclaration()
    {
        var test = CreateTest("""
            public partial class {|#0:Calculator|}
            {
                public int Add(int a, int b) => a + b;
            }

            public partial class Calculator
            {
                public int Sub(int a, int b) => a - b;
            }
            """, EnableRule);
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(0).WithArguments("Calculator"));

        await test.RunAsync();
    }

    /// <summary>
    /// テストフレームワークを参照しているプロジェクト（テストプロジェクト自身）では診断が出ないことを確認する
    /// </summary>
    [Fact]
    public async Task TestProject_DoesNotReportDiagnostic()
    {
        var test = CreateTest("""
            public class CalculatorTests
            {
                public void Add_Works() { }
            }
            """, EnableRule);
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(FactAttribute).Assembly.Location));

        await test.RunAsync();
    }

    /// <summary>
    /// Ignore の Fix で、クラスの直前（ドキュメントコメントの下）に「// Ignore TEST001」が挿入されることを確認する
    /// </summary>
    [Fact]
    public async Task IgnoreFix_InsertsCommentBeforeClass()
    {
        var test = new CSharpCodeFixTest<MissingTest, MissingTestFix, DefaultVerifier>
        {
            TestCode = """
                namespace Product;

                /// <summary>計算</summary>
                public class {|#0:Calculator|}
                {
                    public int Add(int a, int b) => a + b;
                }
                """,
            FixedCode = """
                namespace Product;

                /// <summary>計算</summary>
                // Ignore TEST001
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", EnableRule));
        test.ExpectedDiagnostics.Add(ClassDiagnostic().WithLocation(0).WithArguments("Calculator"));

        await test.RunAsync();
    }

    private static DiagnosticResult ClassDiagnostic()
    {
        return Verify.Diagnostic(MissingTest.ClassRule);
    }

    private static CSharpAnalyzerTest<MissingTest, DefaultVerifier> CreateTest(string source, string globalConfig)
    {
        var test = new CSharpAnalyzerTest<MissingTest, DefaultVerifier> { TestCode = source };
        test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", globalConfig));
        return test;
    }
}
