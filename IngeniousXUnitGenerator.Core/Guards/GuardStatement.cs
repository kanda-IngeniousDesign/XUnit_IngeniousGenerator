namespace IngeniousXUnitGenerator.Core.Guards;

/// <summary>
/// メソッド先頭にあるガード文1つ分（解析できたケースの一覧、または解析できなかった条件の原文）
/// </summary>
public sealed class GuardStatement
{
    private GuardStatement(IReadOnlyList<GuardCase> cases, string? unrecognizedText)
    {
        Cases = cases;
        UnrecognizedText = unrecognizedText;
    }

    /// <summary>解析できたケース（「||」でつながった条件は複数になる）</summary>
    public IReadOnlyList<GuardCase> Cases { get; }

    /// <summary>解析できなかった場合の原文（解析できた場合はnull）</summary>
    public string? UnrecognizedText { get; }

    /// <summary>解析できたガードか</summary>
    public bool IsRecognized => UnrecognizedText is null;

    /// <summary>
    /// 解析できたガード文を作る
    /// </summary>
    /// <param name="cases">ガードのケース</param>
    /// <returns>ガード文</returns>
    public static GuardStatement Recognized(IReadOnlyList<GuardCase> cases)
    {
        return new GuardStatement(cases, null);
    }

    /// <summary>
    /// 例外を投げる形だが、条件を解析できなかったガード文を作る
    /// </summary>
    /// <param name="text">ガードの原文</param>
    /// <returns>ガード文</returns>
    public static GuardStatement Unrecognized(string text)
    {
        return new GuardStatement([], text);
    }
}

/// <summary>
/// メンバー1つ分のガード解析結果
/// </summary>
public sealed class MemberGuards
{
    /// <summary>
    /// ガード解析結果を作成する
    /// </summary>
    /// <param name="statements">メソッド先頭から連続するガード文</param>
    /// <param name="remainingThrowCount">ガードより後ろにあるthrowの数</param>
    public MemberGuards(IReadOnlyList<GuardStatement> statements, int remainingThrowCount)
    {
        Statements = statements;
        RemainingThrowCount = remainingThrowCount;
    }

    /// <summary>メソッド先頭から連続するガード文（実行順）</summary>
    public IReadOnlyList<GuardStatement> Statements { get; }

    /// <summary>ガードより後ろにあるthrowの数（前提状態が分からないため自動生成の対象外）</summary>
    public int RemainingThrowCount { get; }
}
