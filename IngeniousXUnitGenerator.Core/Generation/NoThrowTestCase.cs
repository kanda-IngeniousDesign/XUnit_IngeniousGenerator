using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// 生成する「例外が出ないこと」のテスト（戻り値がない、または戻り値が実行ごとに変わるメソッド向け）
/// </summary>
internal sealed class NoThrowTestCase : PlannedTest
{
    /// <summary>
    /// 例外が出ないことのテストの内容を作成する
    /// </summary>
    /// <param name="member">テスト対象のメソッド</param>
    /// <param name="testName">テストメソッド名</param>
    /// <param name="inputs">テストの入力</param>
    public NoThrowTestCase(IMethodSymbol member, string testName, BoundaryInputs inputs)
        : base(member, testName)
    {
        Inputs = inputs;
    }

    /// <summary>テストの入力</summary>
    public BoundaryInputs Inputs { get; }

    /// <summary>
    /// テストメソッド（と、必要ならテストデータのプロパティ）を組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストクラスに追加するメンバー</returns>
    public override IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder)
    {
        return builder.BuildNoThrowTest(this);
    }
}
