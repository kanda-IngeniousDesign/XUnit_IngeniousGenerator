using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 生成するスナップショットテスト（境界値の入力で実行し、結果を Verify で記録・比較する）
/// </summary>
internal sealed class SnapshotTestCase : PlannedTest
{
    /// <summary>
    /// スナップショットテストの内容を作成する
    /// </summary>
    /// <param name="member">テスト対象のメソッド</param>
    /// <param name="testName">テストメソッド名</param>
    /// <param name="inputs">テストの入力</param>
    public SnapshotTestCase(IMethodSymbol member, string testName, BoundaryInputs inputs)
        : base(member, testName)
    {
        Inputs = inputs;
    }

    /// <summary>テストの入力</summary>
    public BoundaryInputs Inputs { get; }

    /// <summary>
    /// スナップショットテストのメソッド（と、必要ならテストデータのプロパティ）を組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストクラスに追加するメンバー</returns>
    public override IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder)
    {
        return builder.BuildSnapshotTest(this);
    }
}
