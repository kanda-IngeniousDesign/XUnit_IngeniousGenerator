using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// 同じクラスに書かれた関数（private の関数など）の呼び出し。呼び出し元のテストの中で、呼び出し先の中身もたどって評価するために使う
/// </summary>
internal sealed class SameClassCall
{
    /// <summary>呼び出しをたどる深さの上限（深い呼び出しで解析が重くならないようにする）</summary>
    public const int MaxDepth = 8;

    private SameClassCall(IMethodSymbol method, BaseMethodDeclarationSyntax declaration, SemanticModel model, IReadOnlyList<CallArgument> arguments)
    {
        Method = method;
        Declaration = declaration;
        Model = model;
        Arguments = arguments;
    }

    /// <summary>呼び出し先の関数</summary>
    public IMethodSymbol Method { get; }

    /// <summary>呼び出し先の宣言</summary>
    public BaseMethodDeclarationSyntax Declaration { get; }

    /// <summary>呼び出し先の宣言を含むファイルのセマンティックモデル</summary>
    public SemanticModel Model { get; }

    /// <summary>引数（評価する順）</summary>
    public IReadOnlyList<CallArgument> Arguments { get; }

    /// <summary>
    /// 呼び出しが、たどれる同じクラスの関数の呼び出しなら、呼び出し先を求める
    /// （ジェネリック・virtual・async・ref / out / params の引数を持つ関数、別のインスタンスの関数は対象外）
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    /// <param name="model">呼び出し式を含むファイルのセマンティックモデル</param>
    /// <returns>呼び出し（たどれなければnull）</returns>
    public static SameClassCall? Resolve(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method || !IsFollowable(method)) return null;
        if (!IsOwnReceiver(invocation.Expression, model)) return null;

        var caller = model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        if (caller is null || !SymbolEqualityComparer.Default.Equals(caller.OriginalDefinition, method.ContainingType.OriginalDefinition)) return null;

        if (method.DeclaringSyntaxReferences.Length != 1) return null;
        if (method.DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax declaration || declaration.Body is null && declaration.ExpressionBody is null) return null;

        if (model.GetOperation(invocation) is not IInvocationOperation operation) return null;
        var arguments = new List<CallArgument>(operation.Arguments.Length);
        foreach (var argument in operation.Arguments)
        {
            if (argument.Parameter is null) return null;
            switch (argument.ArgumentKind)
            {
                case ArgumentKind.Explicit when argument.Syntax is ArgumentSyntax syntax:
                    arguments.Add(new CallArgument(argument.Parameter, syntax.Expression, null));
                    break;
                case ArgumentKind.DefaultValue when argument.Value.ConstantValue.HasValue:
                    arguments.Add(new CallArgument(argument.Parameter, null, argument.Value.ConstantValue.Value));
                    break;
                default:
                    return null;
            }
        }

        var calleeModel = declaration.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(declaration.SyntaxTree);
        return new SameClassCall(method, declaration, calleeModel, arguments);
    }

    /// <summary>
    /// 本体から呼んでいる同じクラスの関数を、呼び出し先の先まで重複なく集める（深さの上限まで）
    /// </summary>
    /// <param name="declaration">呼び出し元の宣言</param>
    /// <param name="model">呼び出し元のセマンティックモデル</param>
    /// <returns>呼び出し（呼び出し元自身は含まない）</returns>
    public static IEnumerable<SameClassCall> FindReachable(BaseMethodDeclarationSyntax declaration, SemanticModel model)
    {
        var visited = new HashSet<SyntaxNode> { declaration };
        var current = new List<(BaseMethodDeclarationSyntax, SemanticModel)> { (declaration, model) };
        for (var depth = 0; depth < MaxDepth && current.Count != 0; depth++)
        {
            var next = new List<(BaseMethodDeclarationSyntax, SemanticModel)>();
            foreach (var (caller, callerModel) in current)
            {
                foreach (var invocation in caller.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (Resolve(invocation, callerModel) is not { } call || !visited.Add(call.Declaration)) continue;

                    next.Add((call.Declaration, call.Model));
                    yield return call;
                }
            }

            current = next;
        }
    }

    /// <summary>
    /// 中身をたどってよい関数か（呼び出し先が実行時に変わらず、引数がすべて値渡し）を判定する
    /// </summary>
    /// <param name="method">関数</param>
    /// <returns>たどってよければtrue</returns>
    private static bool IsFollowable(IMethodSymbol method)
    {
        return method is { MethodKind: MethodKind.Ordinary, IsGenericMethod: false, IsVirtual: false, IsAbstract: false, IsOverride: false, IsExtern: false, IsAsync: false }
            && method.Parameters.All(p => p is { RefKind: RefKind.None, IsParams: false });
    }

    /// <summary>
    /// 呼び出しの対象が自分自身か（Foo()、this.Foo()、クラス名.Foo()）を判定する
    /// </summary>
    /// <param name="expression">呼び出す関数の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>自分自身ならtrue</returns>
    private static bool IsOwnReceiver(ExpressionSyntax expression, SemanticModel model)
    {
        return expression switch
        {
            IdentifierNameSyntax => true,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => true,
            MemberAccessExpressionSyntax memberAccess => model.GetSymbolInfo(memberAccess.Expression).Symbol is INamedTypeSymbol,
            _ => false,
        };
    }
}

/// <summary>
/// 同じクラスの関数に渡す引数
/// </summary>
internal sealed class CallArgument
{
    /// <summary>
    /// 引数を作成する
    /// </summary>
    /// <param name="parameter">受け取る引数</param>
    /// <param name="expression">渡す式（省略して既定値を使う場合はnull）</param>
    /// <param name="defaultValue">省略したときの既定値</param>
    public CallArgument(IParameterSymbol parameter, ExpressionSyntax? expression, object? defaultValue)
    {
        Parameter = parameter;
        Expression = expression;
        DefaultValue = defaultValue;
    }

    /// <summary>受け取る引数</summary>
    public IParameterSymbol Parameter { get; }

    /// <summary>渡す式（省略して既定値を使う場合はnull）</summary>
    public ExpressionSyntax? Expression { get; }

    /// <summary>省略したときの既定値</summary>
    public object? DefaultValue { get; }
}
