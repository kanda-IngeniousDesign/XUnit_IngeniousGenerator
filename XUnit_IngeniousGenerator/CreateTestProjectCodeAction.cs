using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using XUnit_IngeniousGenerator.Core;

namespace XUnit_IngeniousGenerator;

// テストプロジェクトがないときに、テストプロジェクトを作ってテストコードを生成する。
// Roslyn のコード修正（ApplyChangesOperation）ではプロジェクトを追加できないため、csproj とテストファイルを
// プロダクトのプロジェクトの追加ファイル（AdditionalDocument）として作らせ、「ファイルの作成」として反映させる。
// （VS Code の言語サーバーは ApplyChangesOperation 以外の操作を読み飛ばすため、独自の操作は使わない。
//   どの IDE でも同じ作り方にするため、ソリューションへの追加だけは手動で、コマンドをタイトルとテストファイル先頭で案内する）
public sealed class CreateTestProjectCodeAction : CodeAction
{
    private readonly Document _document;
    private readonly INamedTypeSymbol _targetType;
    private readonly Compilation _compilation;
    private readonly string _testProjectDirectory;
    private readonly IMethodSymbol? _onlyMember;

    public CreateTestProjectCodeAction(
        Document document, INamedTypeSymbol targetType, Compilation compilation, string testProjectDirectory, IMethodSymbol? onlyMember = null)
    {
        _document = document;
        _targetType = targetType;
        _compilation = compilation;
        _testProjectDirectory = testProjectDirectory;
        _onlyMember = onlyMember;
    }

    public override string Title => $"Fix : テストプロジェクト（{TestProjectName}）を作成してテストコードを生成する（ソリューションへの追加は dotnet sln add で）";

    // 一括適用（すべて修正）で複数のプロジェクト作成が同時に走らないよう、等価キーを持たせない
    public override string? EquivalenceKey => null;

    private string TestProjectName => Path.GetFileName(_testProjectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    protected override Task<Solution?> GetChangedSolutionAsync(CancellationToken cancellationToken)
    {
        var (relativePath, code) = TestDocumentWriter.GenerateForNewProject(_document, _targetType, _compilation, TestProjectName, _onlyMember);
        var productProjectPath = _document.Project.FilePath!;

        // VS Code（C# Dev Kit）では Solution.FilePath が内部で生成したソリューションを指すため使わず、
        // プロダクトのプロジェクトから上へたどって、利用者のソリューションを探す
        var solutionPath = TestProjectScaffolder.FindSolution(productProjectPath);
        return Task.FromResult<Solution?>(AddProjectFiles(productProjectPath, solutionPath, relativePath, code));
    }

    /// <summary>
    /// csproj とテストファイルを、プロダクトのプロジェクトの追加ファイルとして加えたソリューションを作る。
    /// VS Code はこれを「ファイルの作成」として反映する（プロダクトの csproj は変更されない）
    /// </summary>
    /// <param name="productProjectPath">プロダクトの csproj のパス</param>
    /// <param name="solutionPath">利用者のソリューションのパス（見つからなければnull）</param>
    /// <param name="testFileRelativePath">テストプロジェクト内のテストファイルの相対パス</param>
    /// <param name="testCode">テストファイルの内容</param>
    /// <returns>ファイルを加えたソリューション</returns>
    private Solution AddProjectFiles(string productProjectPath, string? solutionPath, string testFileRelativePath, string testCode)
    {
        var projectFilePath = Path.Combine(_testProjectDirectory, TestProjectName + ".csproj");
        var addCommand = solutionPath is null
            ? $"dotnet sln add \"{projectFilePath}\""
            : $"dotnet sln \"{solutionPath}\" add \"{projectFilePath}\"";
        var header = $"// ソリューションに追加するには: {addCommand}\r\n";

        var project = _document.Project;
        var solution = project.Solution
            .AddAdditionalDocument(
                DocumentId.CreateNewId(project.Id),
                TestProjectName + ".csproj",
                SourceText.From(TestProjectScaffolder.BuildProjectFile(_testProjectDirectory, productProjectPath)),
                filePath: projectFilePath)
            .AddAdditionalDocument(
                DocumentId.CreateNewId(project.Id),
                Path.GetFileName(testFileRelativePath),
                SourceText.From(header + testCode),
                filePath: Path.Combine(_testProjectDirectory, testFileRelativePath));
        return solution;
    }
}
