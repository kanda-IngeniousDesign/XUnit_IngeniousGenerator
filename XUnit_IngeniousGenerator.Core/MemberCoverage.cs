using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// メンバーのテストの状態
/// </summary>
public enum CoverageStatus
{
    /// <summary>テストクラスに、そのメンバーのテスト（メンバー名_ で始まるテスト）がある</summary>
    Covered,

    /// <summary>テストがなく、自動で生成できるテストがある</summary>
    Generatable,

    /// <summary>テストがなく、自動で生成できない（理由は Reasons）</summary>
    NotGeneratable,

    /// <summary>テストがないが、確かめる処理もない（ガードのないコンストラクタ・set アクセサーなど）</summary>
    NothingToTest,
}

/// <summary>
/// テスト対象のメンバー1つ分の、テストの状態
/// </summary>
public sealed class MemberCoverage
{
    /// <summary>
    /// テストの状態を作成する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ・set アクセサー</param>
    /// <param name="status">テストの状態</param>
    /// <param name="reasons">自動で生成できない理由（NotGeneratable のとき）</param>
    public MemberCoverage(IMethodSymbol member, CoverageStatus status, IReadOnlyList<string> reasons)
    {
        Member = member;
        Status = status;
        Reasons = reasons;
    }

    /// <summary>メソッド・コンストラクタ・set アクセサー</summary>
    public IMethodSymbol Member { get; }

    /// <summary>テストの状態</summary>
    public CoverageStatus Status { get; }

    /// <summary>自動で生成できない理由（NotGeneratable のとき）</summary>
    public IReadOnlyList<string> Reasons { get; }
}
