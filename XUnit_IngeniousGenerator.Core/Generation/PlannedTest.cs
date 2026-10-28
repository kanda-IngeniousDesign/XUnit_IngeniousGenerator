using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 生成するテスト1件分（テストクラスのメンバーとして出力される）
/// </summary>
internal abstract class PlannedTest
{
    /// <summary>
    /// テストの共通部分を設定する
    /// </summary>
    /// <param name="member">テスト対象のメソッド・コンストラクタ</param>
    /// <param name="testName">テストメソッド名（Method_Condition_Expected）</param>
    protected PlannedTest(IMethodSymbol member, string testName)
    {
        Member = member;
        TestName = testName;
    }

    /// <summary>テスト対象のメソッド・コンストラクタ</summary>
    public IMethodSymbol Member { get; }

    /// <summary>テストメソッド名（Method_Condition_Expected）</summary>
    public string TestName { get; }

    /// <summary>テスト対象のインスタンス（_sut）を使うか</summary>
    public virtual bool UsesSut => Member.MethodKind is MethodKind.Ordinary or MethodKind.PropertySet && !Member.IsStatic;

    /// <summary>
    /// テストクラスのメンバーを組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストクラスに追加するメンバー</returns>
    public abstract IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder);
}
