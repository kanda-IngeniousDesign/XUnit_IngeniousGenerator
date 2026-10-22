using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// 静的な評価で、ガードより後ろの throw に到達すると分かった入力の例外テスト
/// </summary>
internal sealed class ComputedExceptionTestCase : PlannedTest
{
    /// <summary>
    /// 例外テストの内容を作成する
    /// </summary>
    /// <param name="member">テスト対象のメソッド</param>
    /// <param name="testName">テストメソッド名</param>
    /// <param name="exceptionType">期待する例外の型</param>
    /// <param name="paramName">期待する ParamName（確定できなければnull）</param>
    /// <param name="inputs">呼び出しに渡す値（引数の順）</param>
    public ComputedExceptionTestCase(IMethodSymbol member, string testName, INamedTypeSymbol exceptionType, string? paramName, IReadOnlyList<object?> inputs)
        : base(member, testName)
    {
        ExceptionType = exceptionType;
        ParamName = paramName;
        Inputs = inputs;
    }

    /// <summary>期待する例外の型</summary>
    public INamedTypeSymbol ExceptionType { get; }

    /// <summary>期待する ParamName（確定できなければnull）</summary>
    public string? ParamName { get; }

    /// <summary>呼び出しに渡す値（引数の順）</summary>
    public IReadOnlyList<object?> Inputs { get; }

    /// <summary>
    /// 例外テストのメソッドを組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストメソッド</returns>
    public override IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder)
    {
        return [builder.BuildComputedExceptionTest(this)];
    }
}
