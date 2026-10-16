using IngeniousXUnitGenerator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IngeniousXUnitGenerator.Tests;

public class TestDocumentWriterTests
{
    private const string CalculatorSource = "namespace Product.Domain; public class Calculator { public int Add(int a, int b) => a + b; }";

    private static readonly MetadataReference CorLib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    private static readonly MetadataReference XUnitCore = MetadataReference.CreateFromFile(typeof(FactAttribute).Assembly.Location);

    [Fact]
    public async Task AddTestDocument_TestProjectFound_AddsFileWithSameFoldersAndTestNamespace()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);
        var (document, targetType, compilation) = await GetTargetAsync(workspace);

        var testProject = Assert.Single(TestProjectFinder.Find(workspace.CurrentSolution, document.Project));
        var newSolution = TestDocumentWriter.AddTestDocument(document, targetType, compilation, testProject.Id);

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
    public async Task AddTestDocument_FileAlreadyExists_LeavesSolutionUnchanged()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        var tests = AddProject(workspace, "Product.Tests", [CorLib, XUnitCore], product.Id);
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(tests.Id), "CalculatorTests.cs", ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From("// 人間が編集したテスト"), VersionStamp.Create()))));
        var (document, targetType, compilation) = await GetTargetAsync(workspace);

        Assert.True(TestDocumentWriter.Exists(workspace.CurrentSolution.GetProject(tests.Id)!, document, targetType));
        var newSolution = TestDocumentWriter.AddTestDocument(document, targetType, compilation, tests.Id);

        var only = Assert.Single(newSolution.GetProject(tests.Id)!.Documents);
        Assert.Equal("// 人間が編集したテスト", (await only.GetTextAsync()).ToString());
    }

    [Fact]
    public void Find_ProjectsWithoutXUnitOrReference_AreExcludedAndResultIsOrderedByName()
    {
        using var workspace = new AdhocWorkspace();
        var product = AddProductProject(workspace);
        AddProject(workspace, "Product.App", [CorLib], product.Id);
        AddProject(workspace, "Other.Tests", [CorLib, XUnitCore]);
        AddProject(workspace, "Product.UnitTests", [CorLib, XUnitCore], product.Id);
        AddProject(workspace, "Product.IntegrationTests", [CorLib, XUnitCore], product.Id);

        var found = TestProjectFinder.Find(workspace.CurrentSolution, workspace.CurrentSolution.GetProject(product.Id)!);

        Assert.Equal(["Product.IntegrationTests", "Product.UnitTests"], found.Select(p => p.Name));
    }

    private static Project AddProductProject(AdhocWorkspace workspace)
    {
        var project = AddProject(workspace, "Product", [CorLib]);
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id), "Calculator.cs", ["Domain"],
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(CalculatorSource), VersionStamp.Create())),
            filePath: Path.Combine("/src", "Product", "Domain", "Calculator.cs")));
        return project;
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

    private static async Task<(Document Document, INamedTypeSymbol TargetType, Compilation Compilation)> GetTargetAsync(AdhocWorkspace workspace)
    {
        var document = workspace.CurrentSolution.Projects.Single(p => p.Name == "Product").Documents.Single();
        var compilation = (await document.Project.GetCompilationAsync())!;
        return (document, compilation.GetTypeByMetadataName("Product.Domain.Calculator")!, compilation);
    }
}
