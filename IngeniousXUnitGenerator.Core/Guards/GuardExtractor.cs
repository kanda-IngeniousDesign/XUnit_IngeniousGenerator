using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Guards;

/// <summary>
/// メソッド・コンストラクタの先頭から連続するガード（引数チェック）を抽出する
/// </summary>
public static class GuardExtractor
{
    /// <summary>
    /// メソッド本体の先頭から、ガードが続く限りガード文を抽出する
    /// </summary>
    /// <param name="declaration">メソッド・コンストラクタの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド・コンストラクタのシンボル</param>
    /// <returns>ガードの解析結果</returns>
    public static MemberGuards Extract(BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member)
    {
        return declaration.Body is null ? new MemberGuards([], 0) : ExtractFromBlock(declaration.Body, model, member);
    }

    /// <summary>
    /// プロパティの set アクセサーの先頭から、ガードが続く限りガード文を抽出する
    /// （式本体は「set =&gt; _x = value ?? throw ...」の形だけを対象にする）
    /// </summary>
    /// <param name="accessor">set アクセサーの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">set アクセサーのシンボル</param>
    /// <returns>ガードの解析結果</returns>
    public static MemberGuards ExtractAccessor(AccessorDeclarationSyntax accessor, SemanticModel model, IMethodSymbol member)
    {
        if (accessor.Body is not null) return ExtractFromBlock(accessor.Body, model, member);
        if (accessor.ExpressionBody?.Expression is not AssignmentExpressionSyntax assignment) return new MemberGuards([], 0);

        var guard = TryParseCoalesceThrow(assignment, model, member);
        return guard is null ? new MemberGuards([], 0) : new MemberGuards([guard], 0);
    }

    /// <summary>
    /// ブロックの先頭から、ガードが続く限りガード文を抽出する
    /// </summary>
    /// <param name="body">本体のブロック</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド・コンストラクタ・アクセサーのシンボル</param>
    /// <returns>ガードの解析結果</returns>
    private static MemberGuards ExtractFromBlock(BlockSyntax body, SemanticModel model, IMethodSymbol member)
    {
        // ガード以外の文が来た時点で打ち切る（それ以降の throw は前提状態が分からないため確実なテストにできない）
        var statements = body.Statements;
        var guards = new List<GuardStatement>();
        var index = 0;
        for (; index < statements.Count; index++)
        {
            var guard = TryParseGuard(statements[index], model, member);
            if (guard is null) break;
            guards.Add(guard);
        }

        var remainingThrowCount = statements.Skip(index).Sum(CountThrows);
        return new MemberGuards(guards, remainingThrowCount);
    }

    /// <summary>
    /// 本体のどこにあっても、例外を投げる条件が読み取れるもの（if-throw と ThrowIf～）をすべて集める
    /// （先頭のガードに限らないため例外テストには使わず、境界値から「例外になる値」を外すために使う）
    /// </summary>
    /// <param name="declaration">メソッド・コンストラクタの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド・コンストラクタのシンボル</param>
    /// <returns>例外を投げる条件</returns>
    public static IReadOnlyList<GuardCase> CollectThrowConditions(BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member)
    {
        var statements = declaration.DescendantNodes().Select(n => n switch
        {
            IfStatementSyntax ifStatement => TryParseIfThrow(ifStatement, model, member),
            ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } => ThrowHelperParser.TryParse(invocation, model, member),
            _ => null,
        });
        return statements.Where(s => s is { IsRecognized: true }).SelectMany(s => s!.Cases).ToList();
    }

    /// <summary>
    /// 文がガードならガード文として解析する
    /// </summary>
    /// <param name="statement">解析する文</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>ガード文（ガードでなければnull）</returns>
    private static GuardStatement? TryParseGuard(StatementSyntax statement, SemanticModel model, IMethodSymbol member)
    {
        return statement switch
        {
            IfStatementSyntax ifStatement => TryParseIfThrow(ifStatement, model, member),
            ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } => ThrowHelperParser.TryParse(invocation, model, member),
            ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment } => TryParseCoalesceThrow(assignment, model, member),
            _ => null,
        };
    }

    /// <summary>
    /// 「if (条件) throw new 例外(...);」を解析する
    /// </summary>
    /// <param name="ifStatement">if文</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>ガード文（throwだけのif文でなければnull）</returns>
    private static GuardStatement? TryParseIfThrow(IfStatementSyntax ifStatement, SemanticModel model, IMethodSymbol member)
    {
        if (ifStatement.Else is not null) return null;

        var throwStatement = ifStatement.Statement switch
        {
            ThrowStatementSyntax single => single,
            BlockSyntax { Statements.Count: 1 } block => block.Statements[0] as ThrowStatementSyntax,
            _ => null,
        };
        if (throwStatement is null) return null;

        var text = ifStatement.Condition.ToString();
        if (throwStatement.Expression is not ObjectCreationExpressionSyntax creation) return GuardStatement.Unrecognized(text);
        if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol exceptionType) return GuardStatement.Unrecognized(text);

        var conditions = ConditionParser.Parse(ifStatement.Condition, model, member);
        if (conditions is null) return GuardStatement.Unrecognized(text);

        var paramName = ParamNameResolver.Resolve(creation, model);
        return GuardStatement.Recognized(conditions
            .Select(c => new GuardCase(c.Parameter, c.Condition, exceptionType, paramName))
            .ToList());
    }

    /// <summary>
    /// 「_field = 引数 ?? throw new 例外(...);」を解析する
    /// </summary>
    /// <param name="assignment">代入式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">ガードがあるメンバー</param>
    /// <returns>ガード文（?? throw の代入でなければnull）</returns>
    private static GuardStatement? TryParseCoalesceThrow(AssignmentExpressionSyntax assignment, SemanticModel model, IMethodSymbol member)
    {
        if (assignment.Right is not BinaryExpressionSyntax coalesce || !coalesce.IsKind(SyntaxKind.CoalesceExpression)) return null;
        if (coalesce.Right is not ThrowExpressionSyntax throwExpression) return null;

        var text = coalesce.ToString();
        var parameter = ConditionParser.GetParameter(coalesce.Left, model, member);
        if (parameter is null || throwExpression.Expression is not ObjectCreationExpressionSyntax creation) return GuardStatement.Unrecognized(text);
        if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol exceptionType) return GuardStatement.Unrecognized(text);

        return GuardStatement.Recognized([new GuardCase(parameter, GuardCondition.Null, exceptionType, ParamNameResolver.Resolve(creation, model))]);
    }

    /// <summary>
    /// 文に含まれる throw の数を数える
    /// </summary>
    /// <param name="statement">数える文</param>
    /// <returns>throw文・throw式の数</returns>
    private static int CountThrows(StatementSyntax statement)
    {
        return statement.DescendantNodesAndSelf().Count(n => n is ThrowStatementSyntax or ThrowExpressionSyntax);
    }
}
