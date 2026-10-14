using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// 生成する例外テスト1件分の内容
/// </summary>
internal sealed class ExceptionTestCase
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
    {
        Member = member;
        TestName = testName;
        ExceptionType = exceptionType;
        ParamName = paramName;
        Arguments = arguments;
    }

    /// <summary>テスト対象のメソッド・コンストラクタ</summary>
    public IMethodSymbol Member { get; }

    /// <summary>テストメソッド名（Method_Condition_Expected）</summary>
    public string TestName { get; }

    /// <summary>期待する例外の型</summary>
    public INamedTypeSymbol ExceptionType { get; }

    /// <summary>期待する ParamName（確定できなければnull）</summary>
    public string? ParamName { get; }

    /// <summary>呼び出しに渡す値（引数の順）</summary>
    public IReadOnlyList<(TestValue Value, ITypeSymbol Type)> Arguments { get; }

    /// <summary>テスト対象のインスタンス（_sut）を使うか</summary>
    public bool UsesSut => Member.MethodKind == MethodKind.Ordinary && !Member.IsStatic;
}
