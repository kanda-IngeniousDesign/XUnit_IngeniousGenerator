using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using XUnit_IngeniousGenerator.Core;

namespace XUnit_IngeniousGenerator;

// テストプロジェクトがないときに、テストプロジェクトを作ってテストコードを生成する。
// Roslyn のコード修正（ApplyChangesOperation）ではプロジェクトを追加できないため、IDE ごとに作り方を変える。
// ・Visual Studio : 独自の操作でフォルダとファイルを書き出し、dotnet sln add でソリューションに追加する（ファイル操作は Core 側で行う）。
//                   追加ファイル（AdditionalDocument）にすると、VS はプロダクトの csproj に項目を書き足し、
//                   存在しないフォルダへの書き込みで例外になるうえ、プロダクト側にテストのファイルが入りビルドエラーになる（実機で確認）
// ・VS Code       : 独自の操作は実行されない（Roslyn の言語サーバーは ApplyChangesOperation 以外を読み飛ばす）ため、
//                   追加ファイルとして csproj とテストファイルを作らせる（ファイルの作成として反映され、csproj は変更されない）。
//                   ソリューションへの追加（dotnet sln add）も実行されないため、Fix の適用でエディターが csproj を書き出すのを
//                   裏で待ってから追加する（以前はテストファイルの先頭に案内コメントを入れていたが、残り続けるうえ重複したため廃止）
public sealed class CreateTestProjectCodeAction : CodeAction
{
    private readonly Document _document;
    private readonly INamedTypeSymbol _targetType;
    private readonly Compilation _compilation;
    private readonly string _testProjectDirectory;
    private readonly bool _useFileEdits;
    private readonly IMethodSymbol? _onlyMember;

    public CreateTestProjectCodeAction(
        Document document, INamedTypeSymbol targetType, Compilation compilation, string testProjectDirectory, bool useFileEdits, IMethodSymbol? onlyMember = null)
    {
        _document = document;
        _targetType = targetType;
        _compilation = compilation;
        _testProjectDirectory = testProjectDirectory;
        _useFileEdits = useFileEdits;
        _onlyMember = onlyMember;
    }

    public override string Title => $"Fix : テストプロジェクト（{TestProjectName}）を作成してテストコードを生成する";

    // 一括適用（すべて修正）で複数のプロジェクト作成が同時に走らないよう、等価キーを持たせない
    public override string? EquivalenceKey => null;

    private string TestProjectName => Path.GetFileName(_testProjectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(BuildOperations(addToSolution: true));
    }

    /// <summary>
    /// 変更のプレビューを作る（Visual Studio の独自の操作はディスクへ書き込むため、プレビューでは何も表示しない。
    /// VS Code でも、プレビューではソリューションへの追加を始めない）
    /// </summary>
    /// <param name="cancellationToken">キャンセル</param>
    /// <returns>プレビューの操作</returns>
    protected override Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_useFileEdits ? BuildOperations(addToSolution: false) : []);
    }

    /// <summary>
    /// テストプロジェクトを作る操作を組み立てる
    /// </summary>
    /// <param name="addToSolution">VS Code で、csproj ができたらソリューションへ追加する処理を始めるか</param>
    /// <returns>操作</returns>
    private IEnumerable<CodeActionOperation> BuildOperations(bool addToSolution)
    {
        var (relativePath, code) = TestDocumentWriter.GenerateForNewProject(_document, _targetType, _compilation, TestProjectName, _onlyMember);
        var productProjectPath = _document.Project.FilePath!;

        // VS Code（C# Dev Kit）では Solution.FilePath が内部で生成したソリューションを指すため使わず、
        // プロダクトのプロジェクトから上へたどって、利用者のソリューションを探す
        var solutionPath = TestProjectScaffolder.FindSolution(productProjectPath);

        if (!_useFileEdits) return [new CreateTestProjectOperation(Title, _testProjectDirectory, productProjectPath, solutionPath, relativePath, code)];

        // VS Code は操作の実行（dotnet sln add）を読み飛ばすため、エディターが csproj を書き出したら、裏でソリューションに追加する
        if (addToSolution) _ = TestProjectScaffolder.AddToSolutionWhenCreatedAsync(solutionPath, Path.Combine(_testProjectDirectory, TestProjectName + ".csproj"));
        return [new ApplyChangesOperation(AddProjectFiles(productProjectPath, relativePath, code))];
    }

    /// <summary>
    /// csproj とテストファイルを、プロダクトのプロジェクトの追加ファイルとして加えたソリューションを作る。
    /// VS Code はこれを「ファイルの作成」として反映する（プロダクトの csproj は変更されない）
    /// </summary>
    /// <param name="productProjectPath">プロダクトの csproj のパス</param>
    /// <param name="testFileRelativePath">テストプロジェクト内のテストファイルの相対パス</param>
    /// <param name="testCode">テストファイルの内容</param>
    /// <returns>ファイルを加えたソリューション</returns>
    private Solution AddProjectFiles(string productProjectPath, string testFileRelativePath, string testCode)
    {
        var projectFilePath = Path.Combine(_testProjectDirectory, TestProjectName + ".csproj");

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
                SourceText.From(testCode),
                filePath: Path.Combine(_testProjectDirectory, testFileRelativePath));
        return solution;
    }

    /// <summary>
    /// Visual Studio 向けの独自の操作：フォルダ・csproj・テストファイルをディスクに書き出し、ソリューションに追加する
    /// </summary>
    private sealed class CreateTestProjectOperation : CodeActionOperation
    {
        private readonly string _title;
        private readonly string _testProjectDirectory;
        private readonly string _productProjectPath;
        private readonly string? _solutionPath;
        private readonly string _testFileRelativePath;
        private readonly string _testCode;

        /// <summary>
        /// 書き出す内容を設定する
        /// </summary>
        /// <param name="title">操作のタイトル</param>
        /// <param name="testProjectDirectory">テストプロジェクトのフォルダ</param>
        /// <param name="productProjectPath">プロダクトの csproj のパス</param>
        /// <param name="solutionPath">利用者のソリューションのパス（見つからなければnull）</param>
        /// <param name="testFileRelativePath">テストプロジェクト内のテストファイルの相対パス</param>
        /// <param name="testCode">テストファイルの内容</param>
        public CreateTestProjectOperation(
            string title, string testProjectDirectory, string productProjectPath, string? solutionPath, string testFileRelativePath, string testCode)
        {
            _title = title;
            _testProjectDirectory = testProjectDirectory;
            _productProjectPath = productProjectPath;
            _solutionPath = solutionPath;
            _testFileRelativePath = testFileRelativePath;
            _testCode = testCode;
        }

        public override string Title => _title;

        /// <summary>
        /// フォルダとファイルを作成し、ソリューションに追加する
        /// </summary>
        /// <param name="workspace">ワークスペース</param>
        /// <param name="cancellationToken">キャンセル</param>
        public override void Apply(Workspace workspace, CancellationToken cancellationToken)
        {
            TestProjectScaffolder.Create(_testProjectDirectory, _productProjectPath, _solutionPath, _testFileRelativePath, _testCode);
        }
    }
}
