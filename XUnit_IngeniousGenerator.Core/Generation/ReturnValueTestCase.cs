using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 生成する戻り値テスト（境界値ごとの入力と期待値の組）
/// </summary>
internal sealed class ReturnValueTestCase : PlannedTest
{
    /// <summary>
    /// 戻り値テストの内容を作成する
    /// </summary>
    /// <param name="member">テスト対象のメソッド</param>
    /// <param name="testName">テストメソッド名</param>
    /// <param name="rows">入力（引数の順）と期待値の組</param>
    public ReturnValueTestCase(IMethodSymbol member, string testName, IReadOnlyList<(IReadOnlyList<object?> Inputs, object? Expected)> rows)
        : base(member, testName)
    {
        Rows = rows;
    }

    /// <summary>入力（引数の順）と期待値の組</summary>
    public IReadOnlyList<(IReadOnlyList<object?> Inputs, object? Expected)> Rows { get; }

    /// <summary>
    /// 戻り値テストのメソッド（と、必要ならテストデータのプロパティ）を組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストクラスに追加するメンバー</returns>
    public override IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder)
    {
        return builder.BuildReturnValueTest(this);
    }
}
