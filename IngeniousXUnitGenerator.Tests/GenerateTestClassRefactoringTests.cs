using IngeniousXUnitGenerator.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;

namespace IngeniousXUnitGenerator.Tests;

public class GenerateTestClassRefactoringTests
{
    private const string CalculatorSource = "namespace Product.Domain; public class Calculator { public int Add(int a, int b) => a + b; }";

    private static readonly MetadataReference CorLib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    private static readonly MetadataReference XUnitCore = MetadataReference.CreateFromFile(typeof(FactAttribute).Assembly.Location);

    [Fact]
    public async Task ComputeRefactorings_CursorOnClassName_AddsTestFileToTestProject()
    {
        using var workspace = new AdhocWorkspace();
        var (product, document) = AddProductProject(workspace);
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);

        var actions = await ComputeAsync(workspace, document.Id, "Calculator");

        var action = Assert.Single(actions);
        Assert.Equal("このクラスのテストコードを生成（Product.Tests）", action.Title);

        var newSolution = await ApplyAsync(action);
        var added = Assert.Single(newSolution.GetProject(tests.Id)!.Documents);
        Assert.Equal("CalculatorTests.cs", added.Name);
        Assert.Equal(["Domain"], added.Folders);
        Assert.Equal(Path.Combine("/src", "Product.Tests", "Domain", "CalculatorTests.cs"), added.FilePath);

        var text = (await added.GetTextAsync()).ToString();
        Assert.Contains("using Product.Domain;", text);
        Assert.Contains("namespace Product.Tests.Domain;", text);
        Assert.Contains("public class CalculatorTests", text);
    }

    [Fact]
    public async Task ComputeRefactorings_ProjectWithoutXUnit_OffersNothing()
    {
        using var workspace = new AdhocWorkspace();
        var (product, document) = AddProductProject(workspace);
        AddProject(workspace, "Product.App", [CorLib], product.Id);

        var actions = await ComputeAsync(workspace, document.Id, "Calculator");

        Assert.Empty(actions);
    }

    [Fact]
    public async Task ComputeRefactorings_TestProjectNotReferencingTarget_OffersNothing()
    {
        using var workspace = new AdhocWorkspace();
        var (_, document) = AddProductProject(workspace);
        AddProject(workspace, "Other.Tests", [CorLib, XUnitCore]);

        var actions = await ComputeAsync(workspace, document.Id, "Calculator");

        Assert.Empty(actions);
    }

    [Fact]
    public async Task ComputeRefactorings_CursorInsideMethod_OffersNothing()
    {
        using var workspace = new AdhocWorkspace();
        var (product, document) = AddProductProject(workspace);
        AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);

        var actions = await ComputeAsync(workspace, document.Id, "a + b");

        Assert.Empty(actions);
    }

    [Fact]
    public async Task ComputeRefactorings_MultipleTestProjects_OffersOnePerProjectInNameOrder()
    {
        using var workspace = new AdhocWorkspace();
        var (product, document) = AddProductProject(workspace);
        AddProject(workspace, "Product.UnitTests", [CorLib, XUnitCore], product.Id);
        AddProject(workspace, "Product.IntegrationTests", [CorLib, XUnitCore], product.Id);

        var actions = await ComputeAsync(workspace, document.Id, "Calculator");

        Assert.Equal(
            ["このクラスのテストコードを生成（Product.IntegrationTests）", "このクラスのテストコードを生成（Product.UnitTests）"],
            actions.Select(a => a.Title));
    }

    [Fact]
    public async Task ApplyAction_TestFileAlreadyExists_LeavesSolutionUnchanged()
    {
        using var workspace = new AdhocWorkspace();
        var (product, document) = AddProductProject(workspace);
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(tests.Id), "CalculatorTests.cs", ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From("// 人間が編集したテスト"), VersionStamp.Create()))));

        var action = Assert.Single(await ComputeAsync(workspace, document.Id, "Calculator"));
        var newSolution = await ApplyAsync(action);

        var only = Assert.Single(newSolution.GetProject(tests.Id)!.Documents);
        Assert.Equal("// 人間が編集したテスト", (await only.GetTextAsync()).ToString());
    }

    private static (Project Project, Document Document) AddProductProject(AdhocWorkspace workspace)
    {
        var project = AddProject(workspace, "Product", [CorLib]);
        var document = workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id), "Calculator.cs", ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(CalculatorSource), VersionStamp.Create())),
            filePath: Path.Combine("/src", "Product", "Domain", "Calculator.cs")));
        return (document.Project, document);
    }

    private static Project AddProject(
        AdhocWorkspace workspace, string name, MetadataReference[] references, ProjectId? referencedProject = null)
    {
        var info = ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), name, name, LanguageNames.CSharp,
            filePath: Path.Combine("/src", name, name + ".csproj"),
            metadataReferences: references,
            projectReferences: referencedProject is null ? [] : [new ProjectReference(referencedProject)])
            .WithDefaultNamespace(name);
        return workspace.AddProject(info);
    }

    private static async Task<List<CodeAction>> ComputeAsync(Workspace workspace, DocumentId documentId, string cursorAt)
    {
        // テストプロジェクト追加後の最新スナップショットから取り直す
        var document = workspace.CurrentSolution.GetDocument(documentId)!;
        var text = await document.GetTextAsync();
        var position = text.ToString().IndexOf(cursorAt, StringComparison.Ordinal);
        var actions = new List<CodeAction>();
        var context = new CodeRefactoringContext(document, new TextSpan(position, 0), actions.Add, CancellationToken.None);

        await new GenerateTestClassRefactoring().ComputeRefactoringsAsync(context);
        return actions;
    }

    private static async Task<Solution> ApplyAsync(CodeAction action)
    {
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        return Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution;
    }
}
