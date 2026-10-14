using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Guards;

/// <summary>
/// 例外のコンストラクタに渡された paramName を求める
/// </summary>
internal static class ParamNameResolver
{
    private const string ParamNameParameter = "paramName";

    /// <summary>
    /// コンストラクタの paramName 引数が定数（nameof を含む）なら、その値を返す
    /// </summary>
    /// <param name="creation">例外の生成式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>ParamName に入る値（確定できなければnull）</returns>
    public static string? Resolve(ObjectCreationExpressionSyntax creation, SemanticModel model)
    {
        if (creation.ArgumentList is null) return null;
        if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor) return null;

        var arguments = creation.ArgumentList.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameter = FindParameter(constructor, arguments[i], i);
            if (parameter?.Name != ParamNameParameter) continue;

            return model.GetConstantValue(arguments[i].Expression).Value as string;
        }

        return null;
    }

    /// <summary>
    /// 実引数に対応する仮引数を求める（名前付き引数にも対応）
    /// </summary>
    /// <param name="constructor">呼び出されたコンストラクタ</param>
    /// <param name="argument">実引数</param>
    /// <param name="position">実引数の位置</param>
    /// <returns>仮引数（見つからなければnull）</returns>
    private static IParameterSymbol? FindParameter(IMethodSymbol constructor, ArgumentSyntax argument, int position)
    {
        if (argument.NameColon is { } nameColon)
        {
            return constructor.Parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.ValueText);
        }

        return position < constructor.Parameters.Length ? constructor.Parameters[position] : null;
    }
}
