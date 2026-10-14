using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// テストクラスのメンバー（Sutフィールド・テストメソッド）の構文を組み立てる
/// </summary>
internal sealed class TestSyntaxBuilder
{
    /// <summary>テスト対象のインスタンスを持つフィールド名</summary>
    public const string SutFieldName = "_sut";

    private const string TasksNamespace = "System.Threading.Tasks";

    private readonly INamedTypeSymbol _targetType;
    private readonly ValueRenderer _renderer;

    /// <summary>
    /// 構文の組み立てを準備する
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="renderer">値・型の出力と名前空間の収集</param>
    public TestSyntaxBuilder(INamedTypeSymbol targetType, ValueRenderer renderer)
    {
        _targetType = targetType;
        _renderer = renderer;
    }

    /// <summary>
    /// private readonly T _sut = new T(...); を作る
    /// </summary>
    /// <param name="sut">テスト対象のインスタンスの作り方</param>
    /// <returns>フィールド宣言</returns>
    public FieldDeclarationSyntax BuildSutField(SutPlan sut)
    {
        var creation = ObjectCreationExpression(_renderer.RenderType(_targetType))
            .WithArgumentList(BuildArguments(sut.Constructor!, sut.Arguments));
        var declarator = VariableDeclarator(SutFieldName).WithInitializer(EqualsValueClause(creation));

        return FieldDeclaration(VariableDeclaration(_renderer.RenderType(_targetType)).AddVariables(declarator))
            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
    }

    /// <summary>
    /// 例外テストのメソッドを作る
    /// </summary>
    /// <param name="test">例外テストの内容</param>
    /// <returns>テストメソッドの宣言</returns>
    public MethodDeclarationSyntax BuildExceptionTest(ExceptionTestCase test)
    {
        var awaitKind = GetAwaitKind(test.Member);
        var isAsync = awaitKind != AwaitKind.None;
        var assertion = BuildThrowsAssertion(test, awaitKind);

        var statements = new List<StatementSyntax>(2);
        if (test.ParamName is not null && InheritsArgumentException(test.ExceptionType))
        {
            // ParamName まで確認し、別の引数のガードで同じ例外型が投げられた場合に誤って成功しないようにする
            statements.Add(LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"))
                .AddVariables(VariableDeclarator("exception").WithInitializer(EqualsValueClause(assertion)))));
            statements.Add(ExpressionStatement(InvocationExpression(
                AssertMember(IdentifierName("Equal")),
                ArgumentList(SeparatedList([
                    Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(test.ParamName))),
                    Argument(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("exception"), IdentifierName("ParamName")))])))));
        }
        else
        {
            statements.Add(ExpressionStatement(assertion));
        }

        if (isAsync) _renderer.AddNamespace(TasksNamespace);
        var method = MethodDeclaration(
                isAsync ? IdentifierName("Task") : PredefinedType(Token(SyntaxKind.VoidKeyword)),
                test.TestName)
            .AddAttributeLists(AttributeList(SingletonSeparatedList(Attribute(IdentifierName("Fact")))))
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .WithBody(Block(statements));

        return isAsync ? method.AddModifiers(Token(SyntaxKind.AsyncKeyword)) : method;
    }

    /// <summary>
    /// Assert.Throws&lt;T&gt;(() =&gt; ...) または await Assert.ThrowsAsync&lt;T&gt;(() =&gt; ...) を作る
    /// </summary>
    /// <param name="test">例外テストの内容</param>
    /// <param name="awaitKind">戻り値の待機の種類</param>
    /// <returns>アサーションの式</returns>
    private ExpressionSyntax BuildThrowsAssertion(ExceptionTestCase test, AwaitKind awaitKind)
    {
        var call = BuildCall(test);

        // ValueTask は Func<Task> に渡せないため、async ラムダで待機して Task にする
        LambdaExpressionSyntax lambda = awaitKind == AwaitKind.ValueTask
            ? ParenthesizedLambdaExpression(AwaitExpression(call)).WithAsyncKeyword(Token(SyntaxKind.AsyncKeyword))
            : ParenthesizedLambdaExpression(call);

        var throws = GenericName(awaitKind == AwaitKind.None ? "Throws" : "ThrowsAsync")
            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(_renderer.RenderType(test.ExceptionType))));
        var invocation = InvocationExpression(AssertMember(throws), ArgumentList(SingletonSeparatedList(Argument(lambda))));

        return awaitKind == AwaitKind.None ? invocation : AwaitExpression(invocation);
    }

    /// <summary>
    /// テスト対象の呼び出し（new T(...) / T.M(...) / _sut.M(...)）を作る
    /// </summary>
    /// <param name="test">例外テストの内容</param>
    /// <returns>呼び出しの式</returns>
    private ExpressionSyntax BuildCall(ExceptionTestCase test)
    {
        var member = test.Member;
        var arguments = BuildArguments(member, test.Arguments);
        if (member.MethodKind == MethodKind.Constructor)
        {
            return ObjectCreationExpression(_renderer.RenderType(_targetType)).WithArgumentList(arguments);
        }

        ExpressionSyntax receiver = member.IsStatic ? _renderer.RenderType(_targetType) : IdentifierName(SutFieldName);
        return InvocationExpression(
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName(member.Name)),
            arguments);
    }

    /// <summary>
    /// 呼び出しの引数リストを作る（同じ引数の数のオーバーロードがあればキャストで呼び分ける）
    /// </summary>
    /// <param name="member">呼び出すメソッド・コンストラクタ</param>
    /// <param name="values">渡す値</param>
    /// <returns>引数リスト</returns>
    private ArgumentListSyntax BuildArguments(IMethodSymbol member, IReadOnlyList<(TestValue Value, ITypeSymbol Type)> values)
    {
        var withCast = MemberDisplay.HasSameArityOverload(member);
        return ArgumentList(SeparatedList(values.Select(v => Argument(_renderer.Render(v.Value, v.Type, withCast)))));
    }

    /// <summary>
    /// Assert.名前 のメンバーアクセスを作る
    /// </summary>
    /// <param name="name">メンバー名</param>
    /// <returns>メンバーアクセスの式</returns>
    private static MemberAccessExpressionSyntax AssertMember(SimpleNameSyntax name)
    {
        return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("Assert"), name);
    }

    /// <summary>
    /// 戻り値の型から、待機の要否と種類を判定する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>待機の種類</returns>
    private static AwaitKind GetAwaitKind(IMethodSymbol member)
    {
        if (member.MethodKind == MethodKind.Constructor) return AwaitKind.None;

        var returnType = member.ReturnType.OriginalDefinition;
        if (returnType.ContainingNamespace?.ToDisplayString() != TasksNamespace) return AwaitKind.None;

        return returnType.Name switch
        {
            "Task" => AwaitKind.Task,
            "ValueTask" => AwaitKind.ValueTask,
            _ => AwaitKind.None,
        };
    }

    /// <summary>
    /// ArgumentException（またはその派生）かを判定する
    /// </summary>
    /// <param name="type">例外の型</param>
    /// <returns>ArgumentException の系統ならtrue</returns>
    private static bool InheritsArgumentException(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.ArgumentException") return true;
        }

        return false;
    }

    /// <summary>
    /// 戻り値の待機の種類
    /// </summary>
    private enum AwaitKind
    {
        /// <summary>待機しない</summary>
        None,

        /// <summary>Task / Task&lt;T&gt;</summary>
        Task,

        /// <summary>ValueTask / ValueTask&lt;T&gt;</summary>
        ValueTask,
    }
}
