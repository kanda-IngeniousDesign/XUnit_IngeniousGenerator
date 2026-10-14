using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Guards;

/// <summary>
/// ガードの1ケース（どの引数が、どの条件のとき、どの例外を投げるか）
/// </summary>
public sealed class GuardCase
{
    /// <summary>
    /// ガードのケースを作成する
    /// </summary>
    /// <param name="parameter">対象の引数</param>
    /// <param name="condition">例外になる条件</param>
    /// <param name="exceptionType">投げられる例外の型</param>
    /// <param name="paramName">例外のParamNameに入る値（確定できない場合はnull）</param>
    public GuardCase(IParameterSymbol parameter, GuardCondition condition, INamedTypeSymbol exceptionType, string? paramName)
    {
        Parameter = parameter;
        Condition = condition;
        ExceptionType = exceptionType;
        ParamName = paramName;
    }

    /// <summary>対象の引数</summary>
    public IParameterSymbol Parameter { get; }

    /// <summary>例外になる条件</summary>
    public GuardCondition Condition { get; }

    /// <summary>投げられる例外の型</summary>
    public INamedTypeSymbol ExceptionType { get; }

    /// <summary>例外のParamNameに入る値（確定できない場合はnull）</summary>
    public string? ParamName { get; }
}
