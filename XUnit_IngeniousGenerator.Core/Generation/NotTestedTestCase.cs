using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// テストしていない関数を、テストエクスプローラーの一覧に理由つきで載せるスキップのテスト（中身は空）
/// </summary>
internal sealed class NotTestedTestCase : PlannedTest
{
    /// <summary>
    /// スキップのテストの内容を作成する
    /// </summary>
    /// <param name="member">テストしていない関数</param>
    /// <param name="reasons">テストしていない理由</param>
    public NotTestedTestCase(IMethodSymbol member, IReadOnlyList<string> reasons)
        : base(member, TestTargets.GetBaseName(member) + TestTargets.NotTestedSuffix)
    {
        Reasons = reasons;
    }

    /// <summary>テストしていない理由</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>テスト対象のインスタンスは使わない</summary>
    public override bool UsesSut => false;

    /// <summary>
    /// スキップのテストメソッドを組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストクラスに追加するメンバー</returns>
    public override IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder)
    {
        return [builder.BuildNotTestedTest(this)];
    }
}
