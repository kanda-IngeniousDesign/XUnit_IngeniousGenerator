using System.Collections.Immutable;
using XUnit_IngeniousGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace XUnit_IngeniousGenerator.Tests.Analyzer;

/// <summary>
/// TEST001 の「テストコードを生成」Fix が、別プロジェクト（テストプロジェクト）にテストファイルを追加・追記することと、
/// TEST002 の Ignore を検証するテスト
/// </summary>
public class GenerateTestFixTests
{
    private const string CalculatorSource = """
        using System;
        namespace Product.Domain;
        public class Calculator
        {
            public int Divide(int dividend, int divisor)
            {
                if (divisor == 0) throw new ArgumentOutOfRangeException(nameof(divisor));
                return dividend / divisor;
            }

            public int Twice(int value) => value * 2;

            private int Helper(int value) => value;
        }
        """;

    private const string GaugeSource = """
        using System;
        namespace Product.Domain;
        public class Gauge
        {
            private int _limit;

            public Gauge(int limit)
            {
                if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
                _limit = limit;
            }

            public int Limit
            {
                get => _limit;
                set
                {
                    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                    _limit = value;
                }
            }
        }
        """;

    private static readonly MetadataReference CorLib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    private static readonly MetadataReference XUnitCore = MetadataReference.CreateFromFile(typeof(FactAttribute).Assembly.Location);

    /// <summary>
    /// Fix に「テストコードを生成」と「Ignore」が出て、生成するとテストプロジェクトに CalculatorTests.cs が追加されることを確認する
    /// </summary>
    [Fact]
    public async Task Fix_TestProjectExists_AddsGeneratedTestFile()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);

        var actions = await ComputeFixesAsync(workspace);

        Assert.Equal(["Fix : テストコードを生成する（Product.Tests）", "Ignore : テストコードの存在チェックを無視する"], actions.Select(a => a.Title));
        var newSolution = await ApplyAsync(actions[0]);
        var added = Assert.Single(newSolution.GetProject(tests.Id)!.Documents);
        Assert.Equal("CalculatorTests.cs", added.Name);
        Assert.Equal(["Domain"], added.Folders);

        var text = (await added.GetTextAsync()).ToString();
        Assert.Contains("namespace Product.Tests.Domain;", text);
        Assert.Contains("public void Divide_DivisorIsZero_ThrowsArgumentOutOfRangeException()", text);
        Assert.Contains("public void Divide_BoundaryValues_ReturnsExpected(int dividend, int divisor, int expected)", text);
    }

    /// <summary>
    /// テストプロジェクトがなければ、「テストプロジェクトを作成して生成」と Ignore が出ることを確認する。
    /// Visual Studio（言語サーバー以外）では、作成はソリューションの変更ではなく、ディスクに書き出す独自の操作になる
    /// （追加ファイルにすると、VS はプロダクトの csproj に項目を書き足してしまうため）
    /// </summary>
    [Fact]
    public async Task Fix_NoTestProject_OffersCreateTestProjectAndIgnore()
    {
        using var workspace = new AdhocWorkspace();
        AddProductProject(workspace);

        var actions = await ComputeFixesAsync(workspace);

        Assert.Equal(
            ["Fix : テストプロジェクト（Product.Tests）を作成してテストコードを生成する", "Ignore : テストコードの存在チェックを無視する"],
            actions.Select(a => a.Title));
        var operation = Assert.Single(await actions[0].GetOperationsAsync(CancellationToken.None));
        Assert.IsNotType<ApplyChangesOperation>(operation);
        Assert.Empty(await actions[0].GetPreviewOperationsAsync(CancellationToken.None));
    }

    /// <summary>
    /// VS Code 向け（独自の操作が実行されない環境）では、csproj とテストファイルを
    /// プロダクトのプロジェクトの追加ファイルとして作らせ（＝ファイルの作成として反映される）、ソリューションへの追加方法をコメントで案内することを確認する
    /// </summary>
    [Fact]
    public async Task CreateTestProject_AddsProjectFileAndTestFileAsNewFiles()
    {
        using var workspace = new AdhocWorkspace();
        AddProductProject(workspace);
        var document = GetCalculatorDocument(workspace);
        var compilation = (await document.Project.GetCompilationAsync())!;
        var targetType = compilation.GetTypeByMetadataName("Product.Domain.Calculator")!;
        var testProjectDirectory = Path.Combine("/src", "Product.Tests");

        var action = new CreateTestProjectCodeAction(document, targetType, compilation, testProjectDirectory, useFileEdits: true);
        var newSolution = await ApplyAsync(action);

        Assert.Equal("Fix : テストプロジェクト（Product.Tests）を作成してテストコードを生成する（ソリューションへの追加は dotnet sln add で）", action.Title);
        var changes = newSolution.GetChanges(workspace.CurrentSolution).GetProjectChanges().Single();
        var added = changes.GetAddedAdditionalDocuments().Select(id => newSolution.GetAdditionalDocument(id)!).OrderBy(d => d.FilePath).ToList();
        Assert.Equal(
            [Path.Combine(testProjectDirectory, "Domain", "CalculatorTests.cs"), Path.Combine(testProjectDirectory, "Product.Tests.csproj")],
            added.Select(d => d.FilePath));
        Assert.Empty(changes.GetAddedDocuments());

        var projectFile = (await added[1].GetTextAsync()).ToString();
        Assert.Contains("<PackageReference Include=\"Verify.Xunit\"", projectFile);
        Assert.Contains("<ProjectReference Include=", projectFile);

        var testCode = (await added[0].GetTextAsync()).ToString();
        Assert.StartsWith("// ソリューションに追加するには: dotnet sln add \"" + Path.Combine(testProjectDirectory, "Product.Tests.csproj") + "\"", testCode);
        Assert.Contains("namespace Product.Tests.Domain;", testCode);
    }

    /// <summary>
    /// 関数名の TEST001 では、その関数のテストだけを既存のテストファイルに追加することを確認する
    /// </summary>
    [Fact]
    public async Task Fix_OnMember_AddsTestsForThatMemberOnly()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);
        var existing = workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(tests.Id), "CalculatorTests.cs", ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From("namespace Product.Tests.Domain;\n\npublic class CalculatorTests\n{\n    // 人間が書いたテスト\n}\n"), VersionStamp.Create()))));

        var actions = await ComputeFixesAsync(workspace, "Twice", MissingTest.MemberRule);

        Assert.Equal(["Fix : この関数のテストコードを追加する（Product.Tests）", "Ignore : テストコードの存在チェックを無視する"], actions.Select(a => a.Title));
        var merged = (await (await ApplyAsync(actions[0])).GetDocument(existing.Id)!.GetTextAsync()).ToString();
        Assert.Contains("// 人間が書いたテスト", merged);
        Assert.Contains("Twice_BoundaryValues_ReturnsExpected", merged);
        Assert.DoesNotContain("Divide", merged);
    }

    /// <summary>
    /// TEST002 には Ignore だけが出て、関数の直前に「// Ignore TEST002」が挿入されることを確認する
    /// </summary>
    [Fact]
    public async Task Fix_NotGeneratable_OffersOnlyIgnoreBeforeMember()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);

        var actions = await ComputeFixesAsync(workspace, "Twice", MissingTest.NotGeneratableRule);

        var action = Assert.Single(actions);
        Assert.Equal("Ignore : 自動生成できないテストの警告を無視する（別の方法で確認済み）", action.Title);
        var document = (await ApplyAsync(action)).Projects.Single(p => p.Name == "Product").Documents.Single();
        Assert.Contains("    // Ignore TEST002\n    public int Twice(int value)", (await document.GetTextAsync()).ToString());
    }

    /// <summary>
    /// ブロック本体を持つ関数では、CSharp_IngeniousAnalyzer の関数のルールと同じく、「// Ignore TEST002」を本体の1行目に挿入することを確認する
    /// </summary>
    [Fact]
    public async Task Fix_NotGeneratableOnBlockBody_InsertsIgnoreAtFirstLineOfBody()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);

        var actions = await ComputeFixesAsync(workspace, "Divide", MissingTest.NotGeneratableRule);

        var document = (await ApplyAsync(Assert.Single(actions))).Projects.Single(p => p.Name == "Product").Documents.Single();
        Assert.Contains("    public int Divide(int dividend, int divisor)\n    {\n        // Ignore TEST002\n        if (divisor == 0)", (await document.GetTextAsync()).ToString());
    }

    /// <summary>
    /// コンストラクタ名・プロパティ名の TEST001 でも、そのメンバーのテストだけを既存のテストファイルに追加することを確認する
    /// （コンストラクタ名はクラス名と同じため、2つ目の出現を警告の位置にする）
    /// </summary>
    [Theory]
    [InlineData("Gauge", 1, "Constructor_LimitIsMinus1_ThrowsArgumentOutOfRangeException", "Limit_")]
    [InlineData("Limit", 0, "Limit_ValueIsMinus1_ThrowsArgumentOutOfRangeException", "Constructor_")]
    public async Task Fix_OnConstructorOrPropertySetter_AddsTestsForThatMemberOnly(string identifierText, int occurrence, string expectedTest, string unexpectedPrefix)
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace, GaugeSource, "Gauge.cs");
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);
        var existing = workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(tests.Id), "GaugeTests.cs", ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From("namespace Product.Tests.Domain;\n\npublic class GaugeTests\n{\n}\n"), VersionStamp.Create()))));

        var actions = await ComputeFixesAsync(workspace, identifierText, MissingTest.MemberRule, occurrence);

        Assert.Equal("Fix : この関数のテストコードを追加する（Product.Tests）", actions[0].Title);
        var merged = (await (await ApplyAsync(actions[0])).GetDocument(existing.Id)!.GetTextAsync()).ToString();
        Assert.Contains(expectedTest, merged);
        Assert.DoesNotContain(unexpectedPrefix, merged);
    }

    private static Task<List<CodeAction>> ComputeFixesAsync(AdhocWorkspace workspace)
    {
        return ComputeFixesAsync(workspace, "Calculator", new MissingTest().SupportedDiagnostics[0]);
    }

    private static async Task<List<CodeAction>> ComputeFixesAsync(AdhocWorkspace workspace, string identifierText, DiagnosticDescriptor descriptor, int occurrence = 0)
    {
        var document = GetCalculatorDocument(workspace);
        var root = (await document.GetSyntaxRootAsync())!;
        var identifier = root.DescendantTokens().Where(t => t.ValueText == identifierText).ElementAt(occurrence);
        var diagnostic = Diagnostic.Create(descriptor, identifier.GetLocation(), identifierText, "x");

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new MissingTestFix().RegisterCodeFixesAsync(context);
        return actions;
    }

    private static async Task<Solution> ApplyAsync(CodeAction action)
    {
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        return Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution;
    }

    private static Document GetCalculatorDocument(AdhocWorkspace workspace)
    {
        return workspace.CurrentSolution.Projects.Single(p => p.Name == "Product").Documents.Single();
    }

    private static Project AddProductProject(AdhocWorkspace workspace, string source = CalculatorSource, string fileName = "Calculator.cs")
    {
        var project = AddProject(workspace, "Product", [CorLib]);
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id), fileName, ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
            filePath: Path.Combine("/src", "Product", "Domain", fileName)));
        return project;
    }

    private static Project AddProject(AdhocWorkspace workspace, string name, MetadataReference[] references, ProjectId? referencedProject = null)
    {
        var info = ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), name, name, LanguageNames.CSharp,
            filePath: Path.Combine("/src", name, name + ".csproj"),
            metadataReferences: references,
            projectReferences: referencedProject is null ? [] : [new ProjectReference(referencedProject)])
            .WithDefaultNamespace(name);
        return workspace.AddProject(info);
    }
}
