using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Guards;

/// <summary>
/// ArgumentNullException.ThrowIfNull などの標準ガードメソッドを解析する
/// </summary>
internal static class ThrowHelperParser
{
    private const string ArgumentNullExceptionName = "System.ArgumentNullException";
    private const string ArgumentExceptionName = "System.ArgumentException";
    private const string ArgumentOutOfRangeExceptionName = "System.ArgumentOutOfRangeException";

    /// <summary>
    /// 呼び出しが ThrowIf で始まる静的メソッドならガードとして解析する
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>ガード文（ガードでなければnull）</returns>
    public static GuardStatement? TryParse(InvocationExpressionSyntax invocation, SemanticModel model, IMethodSymbol member)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { IsStatic: true } helper) return null;
        if (!helper.Name.StartsWith("ThrowIf", StringComparison.Ordinal)) return null;

        var cases = ParseCases(helper, invocation, model, member);
        return cases is null ? GuardStatement.Unrecognized(invocation.ToString()) : GuardStatement.Recognized(cases);
    }

    /// <summary>
    /// 既知のガードメソッドなら、引数ごとのケースに変換する
    /// </summary>
    /// <param name="helper">呼び出されたガードメソッド</param>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>ケースの一覧（未対応のガードメソッドならnull）</returns>
    private static List<GuardCase>? ParseCases(IMethodSymbol helper, InvocationExpressionSyntax invocation, SemanticModel model, IMethodSymbol member)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0 || arguments.Any(a => a.NameColon is not null)) return null;

        var parameter = ConditionParser.GetParameter(arguments[0].Expression, model, member);
        if (parameter is null) return null;

        var conditions = GetConditions(helper, arguments, model);
        if (conditions is null) return null;

        // 明示的な paramName がなければ、CallerArgumentExpression により引数の式（＝引数名）が入る
        var expectedArgumentCount = conditions.Value.ArgumentCount;
        string? paramName = parameter.Name;
        if (expectedArgumentCount < arguments.Count)
        {
            paramName = model.GetConstantValue(arguments[expectedArgumentCount].Expression).Value as string;
        }

        var compilation = model.Compilation;
        var result = new List<GuardCase>(conditions.Value.Items.Count);
        foreach (var (condition, exceptionName) in conditions.Value.Items)
        {
            var exceptionType = compilation.GetTypeByMetadataName(exceptionName);
            if (exceptionType is null) return null;
            result.Add(new GuardCase(parameter, condition, exceptionType, paramName));
        }

        return result;
    }

    /// <summary>
    /// ガードメソッドごとの例外条件と、paramName を除いた引数の数を返す
    /// </summary>
    /// <param name="helper">呼び出されたガードメソッド</param>
    /// <param name="arguments">呼び出しの引数</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>条件と例外型の組（未対応ならnull）</returns>
    private static (IReadOnlyList<(GuardCondition Condition, string ExceptionName)> Items, int ArgumentCount)? GetConditions(
        IMethodSymbol helper, SeparatedSyntaxList<ArgumentSyntax> arguments, SemanticModel model)
    {
        switch (helper.ContainingType.ToDisplayString(), helper.Name)
        {
            case (ArgumentNullExceptionName, "ThrowIfNull"):
                return ([(GuardCondition.Null, ArgumentNullExceptionName)], 1);
            case (ArgumentExceptionName, "ThrowIfNullOrEmpty"):
                return ([(GuardCondition.Null, ArgumentNullExceptionName), (GuardCondition.Empty, ArgumentExceptionName)], 1);
            case (ArgumentExceptionName, "ThrowIfNullOrWhiteSpace"):
                return ([
                    (GuardCondition.Null, ArgumentNullExceptionName),
                    (GuardCondition.Empty, ArgumentExceptionName),
                    (GuardCondition.WhiteSpace, ArgumentExceptionName)], 1);
            case (ArgumentOutOfRangeExceptionName, _):
                return GetRangeCondition(helper.Name, arguments, model);
            default:
                return null;
        }
    }

    /// <summary>
    /// ArgumentOutOfRangeException.ThrowIf～ の例外条件を返す
    /// </summary>
    /// <param name="name">ガードメソッド名</param>
    /// <param name="arguments">呼び出しの引数</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>条件と例外型の組（未対応ならnull）</returns>
    private static (IReadOnlyList<(GuardCondition Condition, string ExceptionName)> Items, int ArgumentCount)? GetRangeCondition(
        string name, SeparatedSyntaxList<ArgumentSyntax> arguments, SemanticModel model)
    {
        // 定数と比べないガード（ThrowIfZero など）
        GuardCondition? condition = name switch
        {
            "ThrowIfZero" => GuardCondition.Compare(CompareOperator.Equal, 0),
            "ThrowIfNegative" => GuardCondition.Compare(CompareOperator.LessThan, 0),
            "ThrowIfNegativeOrZero" => GuardCondition.Compare(CompareOperator.LessThanOrEqual, 0),
            _ => null,
        };
        if (condition is not null) return ([(condition, ArgumentOutOfRangeExceptionName)], 1);

        // 第2引数の定数と比べるガード（ThrowIfGreaterThan など）
        CompareOperator? compareOperator = name switch
        {
            "ThrowIfEqual" => CompareOperator.Equal,
            "ThrowIfNotEqual" => CompareOperator.NotEqual,
            "ThrowIfLessThan" => CompareOperator.LessThan,
            "ThrowIfLessThanOrEqual" => CompareOperator.LessThanOrEqual,
            "ThrowIfGreaterThan" => CompareOperator.GreaterThan,
            "ThrowIfGreaterThanOrEqual" => CompareOperator.GreaterThanOrEqual,
            _ => null,
        };
        if (compareOperator is null || arguments.Count < 2) return null;
        if (!ConditionParser.TryGetNumericConstant(arguments[1].Expression, model, out var constant)) return null;

        return ([(GuardCondition.Compare(compareOperator.Value, constant), ArgumentOutOfRangeExceptionName)], 2);
    }
}
