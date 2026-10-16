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
        var call = BuildCall(test.Member, BuildArguments(test.Member, test.Arguments));

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
    /// 戻り値テスト（[Theory] と、境界値ごとの InlineData または MemberData）を作る
    /// </summary>
    /// <param name="test">戻り値テストの内容</param>
    /// <returns>テストメソッド（decimal を含む場合はテストデータのプロパティも）</returns>
    public IEnumerable<MemberDeclarationSyntax> BuildReturnValueTest(ReturnValueTestCase test)
    {
        var member = test.Member;
        var expectedName = member.Parameters.Any(p => p.Name == "expected") ? "expectedResult" : "expected";
        var parameters = member.Parameters
            .Select(p => Parameter(Identifier(EscapeIdentifier(p.Name))).WithType(_renderer.RenderType(p.Type)))
            .Append(Parameter(Identifier(expectedName)).WithType(_renderer.RenderType(member.ReturnType)));
        var arguments = ArgumentList(SeparatedList(member.Parameters.Select(p => Argument(IdentifierName(EscapeIdentifier(p.Name))))));
        var assertion = ExpressionStatement(InvocationExpression(
            AssertMember(IdentifierName("Equal")),
            ArgumentList(SeparatedList([Argument(IdentifierName(expectedName)), Argument(BuildCall(member, arguments))]))));

        var method = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), test.TestName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters([.. parameters])
            .WithBody(Block(assertion));

        var columnTypes = member.Parameters.Select(p => p.Type).Append(member.ReturnType).ToList();
        var rows = test.Rows.Select(r => r.Inputs.Append(r.Expected).ToList()).ToList();
        return WithTheoryData(method, MemberDisplay.GetTestName(member) + "_BoundaryValues", columnTypes, rows);
    }

    /// <summary>
    /// スナップショットテスト（実行結果を Verify で記録し、2回目以降は記録と比較する）を作る
    /// </summary>
    /// <param name="test">スナップショットテストの内容</param>
    /// <returns>テストメソッド（decimal を含む場合はテストデータのプロパティも）</returns>
    public IEnumerable<MemberDeclarationSyntax> BuildSnapshotTest(SnapshotTestCase test)
    {
        var member = test.Member;
        var inputs = test.Inputs;
        var parameters = inputs.DataParameters.Select(p => Parameter(Identifier(EscapeIdentifier(p.Name))).WithType(_renderer.RenderType(p.Type)));

        // 境界値を変える引数はテストの引数から、それ以外（モックなど）は固定の値を直接渡す
        var withCast = MemberDisplay.HasSameArityOverload(member);
        var arguments = ArgumentList(SeparatedList(member.Parameters.Select(p => Argument(inputs.FixedValues.TryGetValue(p, out var fixedValue)
            ? _renderer.Render(fixedValue, p.Type, withCast)
            : IdentifierName(EscapeIdentifier(p.Name))))));

        ExpressionSyntax verify = InvocationExpression(
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("Verifier"), IdentifierName("Verify")),
            ArgumentList(SingletonSeparatedList(Argument(BuildCall(member, arguments)))));
        if (0 < inputs.DataParameters.Count)
        {
            // 入力ごとに別のファイルへ記録する
            verify = InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, verify, IdentifierName("UseParameters")),
                ArgumentList(SeparatedList(inputs.DataParameters.Select(p => Argument(IdentifierName(EscapeIdentifier(p.Name)))))));
        }

        // 記録がまだない初回だけ自動で記録し、2回目以降は記録との違いを失敗として検出する。
        // CI（ビルドサーバー）では自動記録せず、記録のコミット漏れを失敗として検出する。
        // 引数をすべて名前付きで渡すのは、古いコンパイラでオーバーロードが曖昧にならないようにするため
        var firstRunOnly = SimpleLambdaExpression(
            Parameter(Identifier("verifiedFile")),
            PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("File"), IdentifierName("Exists")),
                ArgumentList(SingletonSeparatedList(Argument(IdentifierName("verifiedFile")))))));
        var autoVerify = InvocationExpression(
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, verify, IdentifierName("AutoVerify")),
            ArgumentList(SeparatedList([
                Argument(firstRunOnly),
                Argument(LiteralExpression(SyntaxKind.FalseLiteralExpression)).WithNameColon(NameColon("includeBuildServer")),
                Argument(LiteralExpression(SyntaxKind.FalseLiteralExpression)).WithNameColon(NameColon("throwException"))])));

        _renderer.AddNamespace("VerifyXunit");
        _renderer.AddNamespace("System.IO");
        _renderer.AddNamespace(TasksNamespace);
        var method = MethodDeclaration(IdentifierName("Task"), test.TestName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters([.. parameters])
            .WithBody(Block(ReturnStatement(autoVerify)));

        if (inputs.DataParameters.Count == 0) return [method.AddAttributeLists(AttributeList(SingletonSeparatedList(Attribute(IdentifierName("Fact")))))];

        var rows = inputs.Rows.Select(r => (IReadOnlyList<object?>)r).ToList();
        return WithTheoryData(method, MemberDisplay.GetTestName(member) + "_SnapshotInputs", inputs.DataParameters.Select(p => p.Type).ToList(), rows);
    }

    /// <summary>
    /// テストメソッドに [Theory] とデータ（InlineData、decimal を含む場合は TheoryData のプロパティ）を付ける
    /// </summary>
    /// <param name="method">テストメソッド</param>
    /// <param name="dataName">TheoryData のプロパティ名</param>
    /// <param name="columnTypes">データの列の型</param>
    /// <param name="rows">データの行</param>
    /// <returns>テストメソッド（と、必要ならテストデータのプロパティ）</returns>
    private IEnumerable<MemberDeclarationSyntax> WithTheoryData(
        MethodDeclarationSyntax method, string dataName, IReadOnlyList<ITypeSymbol> columnTypes, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        // decimal は属性の引数に書けないため、TheoryData のプロパティから渡す
        if (!columnTypes.Any(t => t.SpecialType == SpecialType.System_Decimal))
        {
            var inlineData = rows.Select(r => AttributeList(SingletonSeparatedList(Attribute(
                IdentifierName("InlineData"),
                AttributeArgumentList(SeparatedList(r.Select(v => AttributeArgument(ValueRenderer.RenderClrValue(v)))))))));
            return [method.AddAttributeLists([TheoryAttribute(), .. inlineData])];
        }

        var memberData = AttributeList(SingletonSeparatedList(Attribute(
            IdentifierName("MemberData"),
            AttributeArgumentList(SingletonSeparatedList(AttributeArgument(
                InvocationExpression(IdentifierName("nameof"), ArgumentList(SingletonSeparatedList(Argument(IdentifierName(dataName)))))))))));
        return [BuildTheoryData(dataName, columnTypes, rows), method.AddAttributeLists(TheoryAttribute(), memberData)];
    }

    /// <summary>
    /// public static TheoryData&lt;...&gt; 名前 =&gt; new TheoryData&lt;...&gt; { { 値, ... }, ... }; を作る
    /// </summary>
    /// <param name="dataName">プロパティ名</param>
    /// <param name="columnTypes">データの列の型</param>
    /// <param name="rows">データの行</param>
    /// <returns>プロパティの宣言</returns>
    private PropertyDeclarationSyntax BuildTheoryData(string dataName, IReadOnlyList<ITypeSymbol> columnTypes, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var theoryDataType = GenericName("TheoryData").WithTypeArgumentList(TypeArgumentList(SeparatedList(columnTypes.Select(_renderer.RenderType))));
        var elements = rows.Select(r =>
        {
            var values = r.Select(ValueRenderer.RenderClrValue).ToList();
            return values.Count == 1
                ? values[0]
                : InitializerExpression(SyntaxKind.ComplexElementInitializerExpression, SeparatedList(values));
        });
        var creation = ObjectCreationExpression(theoryDataType)
            .WithInitializer(InitializerExpression(SyntaxKind.CollectionInitializerExpression, SeparatedList(elements)));

        return PropertyDeclaration(theoryDataType, dataName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
            .WithExpressionBody(ArrowExpressionClause(creation))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>
    /// [Theory] 属性を作る
    /// </summary>
    /// <returns>属性リスト</returns>
    private static AttributeListSyntax TheoryAttribute()
    {
        return AttributeList(SingletonSeparatedList(Attribute(IdentifierName("Theory"))));
    }

    /// <summary>
    /// C# のキーワードと同じ名前の識別子に @ を付ける
    /// </summary>
    /// <param name="name">識別子</param>
    /// <returns>エスケープした識別子</returns>
    private static string EscapeIdentifier(string name)
    {
        return SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;
    }

    /// <summary>
    /// テスト対象の呼び出し（new T(...) / T.M(...) / _sut.M(...)）を作る
    /// </summary>
    /// <param name="member">呼び出すメソッド・コンストラクタ</param>
    /// <param name="arguments">引数リスト</param>
    /// <returns>呼び出しの式</returns>
    private ExpressionSyntax BuildCall(IMethodSymbol member, ArgumentListSyntax arguments)
    {
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
