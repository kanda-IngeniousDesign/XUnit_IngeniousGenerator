using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 生成する例外テスト1件分の内容
/// </summary>
internal sealed class ExceptionTestCase : PlannedTest
{
    /// <summary>
    /// 例外テストの内容を作成する
    /// </summary>
    /// <param name="member">テスト対象のメソッド・コンストラクタ</param>
    /// <param name="testName">テストメソッド名</param>
    /// <param name="exceptionType">期待する例外の型</param>
    /// <param name="paramName">期待する ParamName（確定できなければnull）</param>
    /// <param name="arguments">呼び出しに渡す値（引数の順）</param>
    public ExceptionTestCase(
        IMethodSymbol member, string testName, INamedTypeSymbol exceptionType, string? paramName, IReadOnlyList<(TestValue Value, ITypeSymbol Type)> arguments)
        : base(member, testName)
    {
        ExceptionType = exceptionType;
        ParamName = paramName;
        Arguments = arguments;
    }

    /// <summary>期待する例外の型</summary>
    public INamedTypeSymbol ExceptionType { get; }

    /// <summary>期待する ParamName（確定できなければnull）</summary>
    public string? ParamName { get; }

    /// <summary>呼び出しに渡す値（引数の順）</summary>
    public IReadOnlyList<(TestValue Value, ITypeSymbol Type)> Arguments { get; }

    /// <summary>
    /// 例外テストのメソッドを組み立てる
    /// </summary>
    /// <param name="builder">構文の組み立て</param>
    /// <returns>テストメソッド</returns>
    public override IEnumerable<MemberDeclarationSyntax> Build(TestSyntaxBuilder builder)
    {
        return [builder.BuildExceptionTest(this)];
    }
}
