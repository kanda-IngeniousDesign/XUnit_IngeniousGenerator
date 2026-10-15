namespace IngeniousXUnitGenerator.Core.Evaluation;

/// <summary>
/// 静的評価の結果の種類
/// </summary>
public enum EvaluationKind
{
    /// <summary>値を返した</summary>
    Returned,

    /// <summary>例外が発生した（ガードやゼロ除算など）</summary>
    Threw,

    /// <summary>評価できない構文を含む</summary>
    Unsupported,
}

/// <summary>
/// メソッドを静的に評価した結果
/// </summary>
public sealed class EvaluationResult
{
    private EvaluationResult(EvaluationKind kind, object? value, string reason)
    {
        Kind = kind;
        Value = value;
        Reason = reason;
    }

    /// <summary>例外が発生した結果</summary>
    public static EvaluationResult Threw { get; } = new(EvaluationKind.Threw, null, string.Empty);

    /// <summary>結果の種類</summary>
    public EvaluationKind Kind { get; }

    /// <summary>戻り値（Returnedのとき）</summary>
    public object? Value { get; }

    /// <summary>評価できない理由（Unsupportedのとき）</summary>
    public string Reason { get; }

    /// <summary>
    /// 値を返した結果を作る
    /// </summary>
    /// <param name="value">戻り値</param>
    /// <returns>評価結果</returns>
    public static EvaluationResult Returned(object? value)
    {
        return new EvaluationResult(EvaluationKind.Returned, value, string.Empty);
    }

    /// <summary>
    /// 評価できない結果を作る
    /// </summary>
    /// <param name="reason">評価できない理由</param>
    /// <returns>評価結果</returns>
    public static EvaluationResult Unsupported(string reason)
    {
        return new EvaluationResult(EvaluationKind.Unsupported, null, reason);
    }
}

/// <summary>
/// 評価できない構文に出会ったことを伝える（評価を打ち切る）
/// </summary>
internal sealed class UnsupportedSyntaxException : Exception
{
    /// <summary>
    /// 評価できない理由を指定して作成する
    /// </summary>
    /// <param name="reason">評価できない理由</param>
    public UnsupportedSyntaxException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// 評価中のコードが throw したことを伝える
/// </summary>
internal sealed class GuestThrowException : Exception
{
}
