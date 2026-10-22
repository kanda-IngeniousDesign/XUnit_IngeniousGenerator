using Microsoft.CodeAnalysis;

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
    private EvaluationResult(EvaluationKind kind, object? value, string reason, INamedTypeSymbol? exceptionType = null, string? paramName = null, SyntaxNode? throwSite = null)
    {
        Kind = kind;
        Value = value;
        Reason = reason;
        ExceptionType = exceptionType;
        ParamName = paramName;
        ThrowSite = throwSite;
    }

    /// <summary>発生した例外の型（Threwのとき、分かる場合）</summary>
    public INamedTypeSymbol? ExceptionType { get; }

    /// <summary>例外の ParamName（Threwのとき、分かる場合）</summary>
    public string? ParamName { get; }

    /// <summary>例外が発生した構文（throw 文など。Threwのとき、分かる場合）</summary>
    public SyntaxNode? ThrowSite { get; }

    /// <summary>
    /// 例外が発生した結果を作る
    /// </summary>
    /// <param name="exceptionType">例外の型（分からなければnull）</param>
    /// <param name="paramName">例外の ParamName（分からなければnull）</param>
    /// <param name="throwSite">例外が発生した構文（分からなければnull）</param>
    /// <returns>評価結果</returns>
    public static EvaluationResult Threw(INamedTypeSymbol? exceptionType, string? paramName, SyntaxNode? throwSite)
    {
        return new EvaluationResult(EvaluationKind.Threw, null, string.Empty, exceptionType, paramName, throwSite);
    }

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
/// 評価中のコードが throw したことを伝える（例外の型・ParamName・発生した構文が分かれば持つ）
/// </summary>
internal sealed class GuestThrowException : Exception
{
    /// <summary>
    /// 型が分からない例外として作成する
    /// </summary>
    public GuestThrowException()
    {
    }

    /// <summary>
    /// 例外の情報を指定して作成する
    /// </summary>
    /// <param name="exceptionType">例外の型（シンボル）</param>
    /// <param name="exceptionTypeName">例外の型の完全名（シンボルがない場合に使う）</param>
    /// <param name="paramName">例外の ParamName</param>
    /// <param name="site">例外が発生した構文</param>
    public GuestThrowException(INamedTypeSymbol? exceptionType, string? exceptionTypeName, string? paramName, SyntaxNode? site)
    {
        ExceptionType = exceptionType;
        ExceptionTypeName = exceptionTypeName;
        ParamName = paramName;
        Site = site;
    }

    /// <summary>例外の型（シンボル）</summary>
    public INamedTypeSymbol? ExceptionType { get; }

    /// <summary>例外の型の完全名（シンボルがない場合に使う）</summary>
    public string? ExceptionTypeName { get; }

    /// <summary>例外の ParamName</summary>
    public string? ParamName { get; }

    /// <summary>例外が発生した構文</summary>
    public SyntaxNode? Site { get; }
}
