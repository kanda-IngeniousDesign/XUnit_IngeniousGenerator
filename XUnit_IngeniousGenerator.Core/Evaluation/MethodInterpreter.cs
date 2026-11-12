using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// メソッド本体を、具体的な引数で静的に評価する（利用者のコードは実行せず、対応する構文だけを解釈する）
/// </summary>
public sealed class MethodInterpreter
{
    private readonly SemanticModel _model;
    private readonly IMethodSymbol _member;
    private readonly Dictionary<ISymbol, object?> _variables = new(SymbolEqualityComparer.Default);
    private readonly ImmutableHashSet<IMethodSymbol> _callStack;
    private readonly StepBudget _budget;
    private object? _returnValue;

    // テスト対象のインスタンスの状態（フィールド・自動実装プロパティ）。同じクラスの関数の呼び出し先とは共有する
    private InstanceState? _state;

    private MethodInterpreter(SemanticModel model, IMethodSymbol member, ImmutableHashSet<IMethodSymbol> callStack, StepBudget budget)
    {
        _model = model;
        _member = member;
        _callStack = callStack.Add(member);
        _budget = budget;
    }

    /// <summary>
    /// 文の実行結果（次の文へ進むか、return したか）
    /// </summary>
    private enum Flow
    {
        /// <summary>次の文へ進む</summary>
        Normal,

        /// <summary>return した</summary>
        Return,

        /// <summary>switch 文・ループの break に到達した</summary>
        Break,

        /// <summary>ループの continue に到達した</summary>
        Continue,
    }

    /// <summary>
    /// メソッドを指定の引数で評価する
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッドのシンボル</param>
    /// <param name="arguments">引数の値（引数の順）</param>
    /// <param name="state">テスト対象のインスタンスを作った直後の状態（なければフィールドを読む式は計算しない）</param>
    /// <returns>評価結果</returns>
    public static EvaluationResult Evaluate(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member, IReadOnlyList<object?> arguments, InstanceState? state = null)
    {
        var interpreter = new MethodInterpreter(model, member, ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default), new StepBudget());
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[member.Parameters[i]] = ToInterpreterValue(arguments[i]);

        // 入力ごとに写しを使い、関数の中での書き込みがほかの入力の評価に影響しないようにする
        interpreter._state = state?.Copy();

        try
        {
            return interpreter.Run(declaration);
        }
        catch (UnsupportedSyntaxException ex)
        {
            return EvaluationResult.Unsupported(ex.Message);
        }
        catch (GuestThrowException ex)
        {
            var type = ex.ExceptionType ?? (ex.ExceptionTypeName is null ? null : model.Compilation.GetTypeByMetadataName(ex.ExceptionTypeName));
            return EvaluationResult.Threw(type, ex.ParamName, ex.Site);
        }
        catch (ArithmeticException ex)
        {
            // ゼロ除算・decimal のオーバーフローなど、実行時にも例外になる演算
            return EvaluationResult.Threw(model.Compilation.GetTypeByMetadataName(ex.GetType().FullName!), null, null);
        }
    }

    /// <summary>
    /// 残りのスタックが少なければ評価をやめる（パターンの照合など、評価から呼ばれる再帰の処理で使う）
    /// </summary>
    public static void EnsureStack()
    {
        StepBudget.EnsureStack();
    }

    /// <summary>
    /// 引数だけを使う条件（ガードの条件の一部）を評価する
    /// </summary>
    /// <param name="declaration">メソッド・コンストラクタの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド・コンストラクタのシンボル</param>
    /// <param name="arguments">引数の値（引数の順）</param>
    /// <param name="condition">条件</param>
    /// <returns>条件の値（評価できなければnull）</returns>
    public static bool? EvaluateCondition(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member, IReadOnlyList<object?> arguments, ExpressionSyntax condition)
    {
        var interpreter = new MethodInterpreter(model, member, ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default), new StepBudget());
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[member.Parameters[i]] = ToInterpreterValue(arguments[i]);

        try
        {
            return interpreter.EvaluateConverted(condition) as bool?;
        }
        catch (Exception ex) when (ex is UnsupportedSyntaxException or GuestThrowException or ArithmeticException)
        {
            return null;
        }
    }

    /// <summary>
    /// 本体の先頭の文（ガード）だけを実行し、例外にならずに通過するかを判定する（コンストラクタの引数を決めるために使う）
    /// </summary>
    /// <param name="declaration">メソッド・コンストラクタの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド・コンストラクタのシンボル</param>
    /// <param name="arguments">引数の値（引数の順）</param>
    /// <param name="statementCount">実行する先頭の文の数</param>
    /// <returns>通過したらtrue（例外になった、または評価できなかった場合はfalse）</returns>
    public static bool PassesLeadingStatements(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member, IReadOnlyList<object?> arguments, int statementCount)
    {
        if (declaration.Body is null) return false;

        var interpreter = new MethodInterpreter(model, member, ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default), new StepBudget());
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[member.Parameters[i]] = ToInterpreterValue(arguments[i]);

        try
        {
            return declaration.Body.Statements.Take(statementCount).All(s => interpreter.Execute(s) == Flow.Normal);
        }
        catch (Exception ex) when (ex is UnsupportedSyntaxException or GuestThrowException or ArithmeticException)
        {
            return false;
        }
    }

    /// <summary>
    /// 式本体またはブロック本体を評価する
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <returns>評価結果</returns>
    private EvaluationResult Run(BaseMethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is { } arrow) return EvaluationResult.Returned(EvaluateConverted(arrow.Expression));
        if (declaration.Body is null) return EvaluationResult.Unsupported(Messages.Get("UnsupportedNoBody"));

        return Execute(declaration.Body) == Flow.Return
            ? EvaluationResult.Returned(_returnValue)
            : EvaluationResult.Unsupported(Messages.Get("UnsupportedNoReturnPath"));
    }

    /// <summary>
    /// 文を実行する
    /// </summary>
    /// <param name="statement">文</param>
    /// <returns>実行結果</returns>
    private Flow Execute(StatementSyntax statement)
    {
        StepBudget.EnsureStack();
        switch (statement)
        {
            case BlockSyntax block:
                return ExecuteBlock(block);
            case LocalDeclarationStatementSyntax local:
                DeclareLocals(local);
                return Flow.Normal;
            case ReturnStatementSyntax ret:
                _returnValue = ret.Expression is null ? null : EvaluateConverted(ret.Expression);
                return Flow.Return;
            case IfStatementSyntax ifStatement:
                return ExecuteIf(ifStatement);
            case ThrowStatementSyntax throwStatement:
                throw CreateThrow(throwStatement.Expression, throwStatement);
            case ExpressionStatementSyntax expressionStatement:
                ExecuteExpressionStatement(expressionStatement.Expression);
                return Flow.Normal;
            case EmptyStatementSyntax:
                return Flow.Normal;
            case SwitchStatementSyntax switchStatement:
                return ExecuteSwitch(switchStatement);
            case BreakStatementSyntax:
                return Flow.Break;
            case ContinueStatementSyntax:
                return Flow.Continue;
            case ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                return ExecuteLoop(statement);
            default:
                throw new UnsupportedSyntaxException(DescribeStatement(statement));
        }
    }

    /// <summary>
    /// ブロックの文を順に実行する（return・break に到達したら、そこで抜ける）
    /// </summary>
    /// <param name="block">ブロック</param>
    /// <returns>実行結果</returns>
    private Flow ExecuteBlock(BlockSyntax block)
    {
        foreach (var statement in block.Statements)
        {
            var flow = Execute(statement);
            if (flow != Flow.Normal) return flow;
        }

        return Flow.Normal;
    }

    /// <summary>
    /// switch 文を実行する（case の定数・パターンで分岐し、break で抜ける。when 句は対象外）
    /// </summary>
    /// <param name="switchStatement">switch 文</param>
    /// <returns>実行結果（break は switch の中で消化する）</returns>
    private Flow ExecuteSwitch(SwitchStatementSyntax switchStatement)
    {
        var value = EvaluateConverted(switchStatement.Expression);
        var section = FindSwitchSection(switchStatement, value);
        if (section is null) return Flow.Normal;

        foreach (var statement in section.Statements)
        {
            var flow = Execute(statement);
            if (flow == Flow.Break) return Flow.Normal;
            if (flow is Flow.Return or Flow.Continue) return flow;
        }

        return Flow.Normal;
    }

    /// <summary>
    /// 値に合う switch のセクションを探す（合うものがなければ default）
    /// </summary>
    /// <param name="switchStatement">switch 文</param>
    /// <param name="value">switch の値</param>
    /// <returns>実行するセクション（なければnull）</returns>
    private SwitchSectionSyntax? FindSwitchSection(SwitchStatementSyntax switchStatement, object? value)
    {
        var matcher = new PatternMatcher(_model);
        var matched = switchStatement.Sections.FirstOrDefault(s => s.Labels.Any(l => LabelMatches(l, value, matcher)));
        return matched ?? switchStatement.Sections.FirstOrDefault(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
    }

    /// <summary>
    /// case ラベル（定数・パターン）が値に合うかを判定する（default は合わないものとして扱う）
    /// </summary>
    /// <param name="label">case ラベル</param>
    /// <param name="value">switch の値</param>
    /// <param name="matcher">パターンの評価</param>
    /// <returns>合えばtrue</returns>
    private bool LabelMatches(SwitchLabelSyntax label, object? value, PatternMatcher matcher)
    {
        switch (label)
        {
            case CaseSwitchLabelSyntax caseLabel:
                var constant = _model.GetConstantValue(caseLabel.Value);
                if (!constant.HasValue) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonConstantCase", caseLabel.Value));
                return ClrOperations.ValuesEqual(value, constant.Value);
            case CasePatternSwitchLabelSyntax patternLabel:
                if (patternLabel.WhenClause is not null) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedSwitchStatementWhen"));
                return matcher.Matches(patternLabel.Pattern, value);
            default:
                return false;
        }
    }

    /// <summary>
    /// if 文を実行する
    /// </summary>
    /// <param name="ifStatement">if 文</param>
    /// <returns>実行結果</returns>
    private Flow ExecuteIf(IfStatementSyntax ifStatement)
    {
        if (ToBoolean(EvaluateConverted(ifStatement.Condition))) return Execute(ifStatement.Statement);
        return ifStatement.Else is null ? Flow.Normal : Execute(ifStatement.Else.Statement);
    }

    /// <summary>
    /// ローカル変数を宣言し、初期値を評価する
    /// </summary>
    /// <param name="local">ローカル変数の宣言文</param>
    private void DeclareLocals(LocalDeclarationStatementSyntax local)
    {
        DeclareVariables(local.Declaration);
    }

    /// <summary>
    /// 変数の宣言（初期値つき）を実行する
    /// </summary>
    /// <param name="declaration">変数の宣言</param>
    private void DeclareVariables(VariableDeclarationSyntax declaration)
    {
        foreach (var variable in declaration.Variables)
        {
            if (variable.Initializer is null) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedUninitializedLocal", variable.Identifier.ValueText));

            var symbol = _model.GetDeclaredSymbol(variable) ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedLocal", variable.Identifier.ValueText));
            _variables[symbol] = EvaluateConverted(variable.Initializer.Value);
        }
    }

    /// <summary>
    /// 式文（ガードメソッドの呼び出し、ローカル変数への代入）を実行する
    /// </summary>
    /// <param name="expression">式文の式</param>
    private void ExecuteExpressionStatement(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case InvocationExpressionSyntax invocation when SameClassCall.Resolve(invocation, _model) is { } call:
                Call(call);
                return;
            case InvocationExpressionSyntax invocation when ThrowHelperParser.TryParse(invocation, _model, _member) is not null:
                ExecuteGuardHelper(invocation);
                return;
            case InvocationExpressionSyntax invocation:
                // 戻り値を使わない呼び出し（list.Add(x) など）
                EvaluateInvocation(invocation);
                return;
            case AssignmentExpressionSyntax assignment:
                Assign(assignment);
                return;
            case PostfixUnaryExpressionSyntax { Operand: var operand } postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                Increment(operand, postfix.IsKind(SyntaxKind.PostIncrementExpression));
                return;
            case PrefixUnaryExpressionSyntax { Operand: var operand } prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                Increment(operand, prefix.IsKind(SyntaxKind.PreIncrementExpression));
                return;
            default:
                throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpressionStatement", Shorten(expression)));
        }
    }

    /// <summary>
    /// ArgumentNullException.ThrowIfNull などのガードメソッドを評価する
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    private void ExecuteGuardHelper(InvocationExpressionSyntax invocation)
    {
        var guard = ThrowHelperParser.TryParse(invocation, _model, _member);
        if (guard is null) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", Shorten(invocation)));
        if (!guard.IsRecognized)
        {
            ExecuteComparisonHelper(invocation);
            return;
        }

        foreach (var guardCase in guard.Cases)
        {
            if (guardCase.Condition.IsViolatedBy(ToTestValue(_variables[guardCase.Parameter])))
            {
                throw new GuestThrowException(guardCase.ExceptionType, null, guardCase.ParamName, invocation);
            }
        }
    }

    /// <summary>
    /// 値で比べる ThrowIf～（ThrowIfGreaterThan(min, max) など、ガードとして解析できない引数どうしの比較）を、引数を評価して実行する
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    private void ExecuteComparisonHelper(InvocationExpressionSyntax invocation)
    {
        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol helper || invocation.ArgumentList.Arguments.Count is 0 or > 2)
        {
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", Shorten(invocation)));
        }

        var arguments = invocation.ArgumentList.Arguments.Select(a => EvaluateConverted(a.Expression)).ToList();
        var value = arguments[0];
        var other = arguments.Count == 2 ? arguments[1] : 0;
        var comparison = CompareValues(value, other);
        var throws = (helper.Name, arguments.Count) switch
        {
            ("ThrowIfZero", 1) => comparison == 0,
            ("ThrowIfNegative", 1) => comparison < 0,
            ("ThrowIfNegativeOrZero", 1) => comparison <= 0,
            ("ThrowIfEqual", 2) => comparison == 0,
            ("ThrowIfNotEqual", 2) => comparison != 0,
            ("ThrowIfLessThan", 2) => comparison < 0,
            ("ThrowIfLessThanOrEqual", 2) => comparison <= 0,
            ("ThrowIfGreaterThan", 2) => 0 < comparison,
            ("ThrowIfGreaterThanOrEqual", 2) => 0 <= comparison,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", Shorten(invocation))),
        };

        // ParamName は、最初の引数に書いた式（CallerArgumentExpression）になる
        if (throws) throw new GuestThrowException(helper.ContainingType, null, invocation.ArgumentList.Arguments[0].Expression.ToString(), invocation);
    }

    /// <summary>
    /// 数値どうし・日付どうしを比べる
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="other">比べる値</param>
    /// <returns>value が小さければ負、等しければ0、大きければ正</returns>
    private static int CompareValues(object? value, object? other)
    {
        if (ClrOperations.IsNumber(value) && ClrOperations.IsNumber(other)) return ClrOperations.CompareNumbers(value!, other!);
        if (value is DateOnlyValue date && other is DateOnlyValue otherDate) return date.CompareTo(otherDate);
        if (value is IComparable comparable && value is DateTime or TimeSpan && value.GetType() == other?.GetType()) return comparable.CompareTo(other);

        throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonNumericComparison"));
    }

    /// <summary>
    /// ローカル変数・引数への代入（=, +=, -= など）を実行する
    /// </summary>
    /// <param name="assignment">代入式</param>
    private void Assign(AssignmentExpressionSyntax assignment)
    {
        if (assignment.Left is ElementAccessExpressionSyntax element)
        {
            AssignElement(assignment, element);
            return;
        }

        // ほかのオブジェクト（引数のクラスなど）のプロパティへの代入は、入力の値を書き換えるため対象外にする
        if (assignment.Left is MemberAccessExpressionSyntax { Expression: not ThisExpressionSyntax })
        {
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldAssignment", Shorten(assignment)));
        }

        var target = _model.GetSymbolInfo(assignment.Left).Symbol;
        if (target is not null && _state is not null && _state.Contains(target))
        {
            AssignState(assignment, target);
            return;
        }

        if (target is not (ILocalSymbol or IParameterSymbol) || !_variables.ContainsKey(target))
        {
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldAssignment", Shorten(assignment)));
        }

        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            _variables[target] = EvaluateConverted(assignment.Right);
            return;
        }

        // 複合代入は「左辺 演算子 右辺」を計算してから、左辺の型に戻す
        var op = GetOperator(assignment);
        var left = ClrOperations.Convert(_variables[target], op.Parameters[0].Type);
        var right = ClrOperations.Convert(EvaluateConverted(assignment.Right), op.Parameters[1].Type);
        var result = ClrOperations.Binary(assignment.Kind(), op.Parameters[0].Type, left, right);
        _variables[target] = ClrOperations.Convert(result, target is ILocalSymbol local ? local.Type : ((IParameterSymbol)target).Type);
    }

    /// <summary>
    /// 式を評価し、代入先などの型（ConvertedType）へ暗黙変換した値を返す
    /// </summary>
    /// <param name="expression">式</param>
    /// <returns>変換後の値</returns>
    private object? EvaluateConverted(ExpressionSyntax expression)
    {
        var value = Evaluate(expression);
        var typeInfo = _model.GetTypeInfo(expression);
        if (typeInfo.ConvertedType is null || SymbolEqualityComparer.Default.Equals(typeInfo.Type, typeInfo.ConvertedType)) return value;

        return ClrOperations.Convert(value, typeInfo.ConvertedType);
    }

    /// <summary>
    /// 式を評価する（定数はコンパイラの計算結果を使う）
    /// </summary>
    /// <param name="expression">式</param>
    /// <returns>式の値</returns>
    private object? Evaluate(ExpressionSyntax expression)
    {
        _budget.Consume();
        var constant = _model.GetConstantValue(expression);
        if (constant.HasValue) return constant.Value;

        return expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => Evaluate(parenthesized.Expression),
            IdentifierNameSyntax identifier => ReadVariable(identifier),
            BinaryExpressionSyntax binary => EvaluateBinary(binary),
            PrefixUnaryExpressionSyntax unary => EvaluateUnary(unary),
            ConditionalExpressionSyntax conditional => ToBoolean(EvaluateConverted(conditional.Condition))
                ? EvaluateConverted(conditional.WhenTrue)
                : EvaluateConverted(conditional.WhenFalse),
            CastExpressionSyntax cast => ClrOperations.Convert(EvaluateConverted(cast.Expression), _model.GetTypeInfo(cast).Type!),
            InvocationExpressionSyntax invocation => EvaluateInvocation(invocation),
            ElementAccessExpressionSyntax elementAccess => ReadElement(elementAccess),
            ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax or CollectionExpressionSyntax => CreateCollection(expression),
            BaseObjectCreationExpressionSyntax listCreation when IsList(_model.GetTypeInfo(listCreation).Type) => CreateList(listCreation),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } thisAccess =>
                ReadState(_model.GetSymbolInfo(thisAccess).Symbol, thisAccess.Name.Identifier.ValueText),
            MemberAccessExpressionSyntax objectAccess when IsObjectMember(objectAccess) => ReadObjectMember(objectAccess),
            MemberAccessExpressionSyntax memberAccess => KnownMethods.ReadMember(memberAccess, _model, EvaluateConverted),
            BaseObjectCreationExpressionSyntax creation when TemporalValues.KindOf(_model.GetTypeInfo(creation).Type) != TemporalKind.None =>
                TemporalMethods.Create(creation, _model, EvaluateConverted),
            SwitchExpressionSyntax switchExpression => EvaluateSwitch(switchExpression),
            IsPatternExpressionSyntax isPattern => new PatternMatcher(_model).Matches(isPattern.Pattern, EvaluateConverted(isPattern.Expression)),
            ThrowExpressionSyntax throwExpression => throw CreateThrow(throwExpression.Expression, throwExpression),
            InterpolatedStringExpressionSyntax interpolated => TextFormatting.Interpolate(interpolated, _model, EvaluateConverted),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpression", Shorten(expression))),
        };
    }

    /// <summary>
    /// 同じクラスの関数を呼び出し、その中身も評価する（再帰と、深すぎる呼び出しは対象外）
    /// </summary>
    /// <param name="call">呼び出し</param>
    /// <returns>戻り値（void の関数はnull）</returns>
    private object? Call(SameClassCall call)
    {
        if (_callStack.Contains(call.Method)) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedRecursion", call.Method.Name));
        if (SameClassCall.MaxDepth <= _callStack.Count) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedDeepCall", call.Method.Name));

        var callee = new MethodInterpreter(call.Model, call.Method, _callStack, _budget) { _state = _state };
        foreach (var argument in call.Arguments)
        {
            callee._variables[argument.Parameter] = argument.Expression is null ? argument.DefaultValue : EvaluateConverted(argument.Expression);
        }

        return callee.RunCallee(call.Declaration);
    }

    /// <summary>
    /// 呼び出された関数の本体を評価する（例外は呼び出し元へそのまま伝える）
    /// </summary>
    /// <param name="declaration">関数の宣言</param>
    /// <returns>戻り値（void の関数はnull）</returns>
    private object? RunCallee(BaseMethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is { } arrow)
        {
            if (!_member.ReturnsVoid) return EvaluateConverted(arrow.Expression);

            ExecuteExpressionStatement(arrow.Expression);
            return null;
        }

        var flow = Execute(declaration.Body!);
        if (flow == Flow.Return) return _returnValue;
        if (_member.ReturnsVoid) return null;
        throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNoReturnPathIn", _member.Name));
    }

    /// <summary>
    /// 引数・ローカル変数の値を読む
    /// </summary>
    /// <param name="identifier">識別子</param>
    /// <returns>値</returns>
    private object? ReadVariable(IdentifierNameSyntax identifier)
    {
        var symbol = _model.GetSymbolInfo(identifier).Symbol;
        if (symbol is not null && _variables.TryGetValue(symbol, out var value))
        {
            // 値の分からない引数（コンストラクタに渡したモックなど）は、計算に使えない
            return ReferenceEquals(value, InstanceState.Unknown)
                ? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", identifier.Identifier.ValueText))
                : value;
        }

        return ReadState(symbol, identifier.Identifier.ValueText);
    }

    /// <summary>
    /// フィールド・プロパティの値を、インスタンスの状態から読む（本体のある get は、同じクラスの関数と同じく中身を評価する）
    /// </summary>
    /// <param name="symbol">フィールド・プロパティ</param>
    /// <param name="name">レポート用の名前</param>
    /// <returns>値</returns>
    private object? ReadState(ISymbol? symbol, string name)
    {
        if (symbol is not null && _state is not null && _state.Contains(symbol))
        {
            return _state.TryGet(symbol, out var value) ? value : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", name));
        }

        if (symbol is IPropertySymbol { GetMethod: { } getter } property && _state is not null
            && SymbolEqualityComparer.Default.Equals(property.ContainingType, _state.Type))
        {
            return ReadComputedProperty(getter, name, _state);
        }

        throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", name));
    }

    /// <summary>
    /// ソースにあるクラスのインスタンスのフィールド・プロパティの参照（c.Age など）かを判定する
    /// </summary>
    /// <param name="memberAccess">メンバーアクセス式</param>
    /// <returns>参照ならtrue</returns>
    private bool IsObjectMember(MemberAccessExpressionSyntax memberAccess)
    {
        var symbol = _model.GetSymbolInfo(memberAccess).Symbol;
        return symbol is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false }
            && symbol.ContainingType.Locations.Any(l => l.IsInSource);
    }

    /// <summary>
    /// クラス（DTO）の値のフィールド・プロパティを読む（null なら実行時と同じく NullReferenceException）
    /// </summary>
    /// <param name="memberAccess">メンバーアクセス式</param>
    /// <returns>値</returns>
    private object? ReadObjectMember(MemberAccessExpressionSyntax memberAccess)
    {
        var symbol = _model.GetSymbolInfo(memberAccess).Symbol!;
        var name = memberAccess.Name.Identifier.ValueText;
        var receiver = EvaluateConverted(memberAccess.Expression);
        if (receiver is null) throw new GuestThrowException(null, "System.NullReferenceException", null, memberAccess);
        if (receiver is not ObjectValue instance) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMember", memberAccess));

        var state = GetObjectState(instance);
        if (state.Contains(symbol))
        {
            return state.TryGet(symbol, out var value) ? value : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", name));
        }

        return symbol is IPropertySymbol { GetMethod: { } getter }
            ? ReadComputedProperty(getter, name, state)
            : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", name));
    }

    /// <summary>
    /// クラス（DTO）の値の状態を、コンストラクタと初期化子のプロパティから求める（一度求めたら使い回す）
    /// </summary>
    /// <param name="instance">クラスの値</param>
    /// <returns>状態</returns>
    private InstanceState GetObjectState(ObjectValue instance)
    {
        if (instance.State is InstanceState cached) return cached;

        var state = CreateState(instance.Type, instance.Constructor, instance.Arguments, _model.Compilation);
        foreach (var (property, value) in instance.Properties) state.Set(property, ToInterpreterValue(value));
        instance.State = state;
        return state;
    }

    /// <summary>
    /// 本体のある get（public decimal Total => _price * _count; など）を評価する
    /// </summary>
    /// <param name="getter">get アクセサー</param>
    /// <param name="name">レポート用の名前</param>
    /// <param name="state">プロパティを持つインスタンスの状態</param>
    /// <returns>値</returns>
    private object? ReadComputedProperty(IMethodSymbol getter, string name, InstanceState state)
    {
        var syntax = getter.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        var (expression, block) = syntax switch
        {
            ArrowExpressionClauseSyntax arrow => (arrow.Expression, (BlockSyntax?)null),
            AccessorDeclarationSyntax { ExpressionBody: { } arrow } => (arrow.Expression, null),
            AccessorDeclarationSyntax { Body: { } body } => (null, body),
            _ => ((ExpressionSyntax?)null, (BlockSyntax?)null),
        };
        if (expression is null && block is null) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", name));
        if (_callStack.Contains(getter)) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedRecursion", name));
        if (SameClassCall.MaxDepth <= _callStack.Count) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedDeepCall", name));

        var callee = new MethodInterpreter(_model.Compilation.GetSemanticModel(syntax!.SyntaxTree), getter, _callStack, _budget) { _state = state };
        if (expression is not null) return callee.EvaluateConverted(expression);

        return callee.Execute(block!) == Flow.Return
            ? callee._returnValue
            : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNoReturnPathIn", name));
    }

    /// <summary>
    /// 二項演算を評価する（&amp;&amp; と || は短絡評価）
    /// </summary>
    /// <param name="binary">二項演算の式</param>
    /// <returns>演算結果</returns>
    private object? EvaluateBinary(BinaryExpressionSyntax binary)
    {
        switch (binary.Kind())
        {
            case SyntaxKind.LogicalAndExpression:
                return ToBoolean(EvaluateConverted(binary.Left)) && ToBoolean(EvaluateConverted(binary.Right));
            case SyntaxKind.LogicalOrExpression:
                return ToBoolean(EvaluateConverted(binary.Left)) || ToBoolean(EvaluateConverted(binary.Right));
            case SyntaxKind.CoalesceExpression:
                return EvaluateConverted(binary.Left) ?? EvaluateConverted(binary.Right);
        }

        var op = GetOperator(binary);
        var left = ClrOperations.Convert(EvaluateOperand(binary.Left, op), op.Parameters[0].Type);
        var right = ClrOperations.Convert(EvaluateOperand(binary.Right, op), op.Parameters[1].Type);
        return ClrOperations.Binary(binary.Kind(), op.Parameters[0].Type, left, right);
    }

    /// <summary>
    /// 二項演算の片方を評価する。文字列の連結では、列挙型の値（評価中は整数）をメンバー名にする（unit + "=" なら "Meter="）
    /// </summary>
    /// <param name="operand">片方の式</param>
    /// <param name="op">演算子</param>
    /// <returns>値</returns>
    private object? EvaluateOperand(ExpressionSyntax operand, IMethodSymbol op)
    {
        var value = EvaluateConverted(operand);

        // 型を調べるのは文字列の連結のときだけにする（すべての演算で調べると、呼び出しの多いクラスで重くなる）
        if (op.ContainingType.SpecialType is not (SpecialType.System_String or SpecialType.System_Object) || value is not (int or long)) return value;

        var type = _model.GetTypeInfo(operand).Type;
        return type?.TypeKind == TypeKind.Enum ? TextFormatting.ToText(value, type) : value;
    }

    /// <summary>
    /// 単項演算（-x, +x, !x）を評価する
    /// </summary>
    /// <param name="unary">単項演算の式</param>
    /// <returns>演算結果</returns>
    private object EvaluateUnary(PrefixUnaryExpressionSyntax unary)
    {
        if (unary.IsKind(SyntaxKind.LogicalNotExpression)) return !ToBoolean(EvaluateConverted(unary.Operand));

        var op = GetOperator(unary);
        return ClrOperations.Unary(unary.Kind(), ClrOperations.Convert(EvaluateConverted(unary.Operand), op.Parameters[0].Type));
    }

    /// <summary>
    /// switch 式を評価する（when 句は対象外、どの腕にも合わなければ例外）
    /// </summary>
    /// <param name="switchExpression">switch 式</param>
    /// <returns>合致した腕の値</returns>
    private object? EvaluateSwitch(SwitchExpressionSyntax switchExpression)
    {
        var value = EvaluateConverted(switchExpression.GoverningExpression);
        var matcher = new PatternMatcher(_model);
        foreach (var arm in switchExpression.Arms)
        {
            if (arm.WhenClause is not null) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedSwitchExpressionWhen"));
            if (matcher.Matches(arm.Pattern, value)) return EvaluateConverted(arm.Expression);
        }

        throw new GuestThrowException(null, "System.Runtime.CompilerServices.SwitchExpressionException", null, switchExpression);
    }

    /// <summary>
    /// throw の式から、例外の型と ParamName を持つ例外を作る（new 以外の throw は型が分からないものとして扱う）
    /// </summary>
    /// <param name="thrown">throw する式</param>
    /// <param name="site">throw 文・throw 式</param>
    /// <returns>評価中のコードの例外</returns>
    private GuestThrowException CreateThrow(ExpressionSyntax? thrown, SyntaxNode site)
    {
        if (thrown is not ObjectCreationExpressionSyntax creation) return new GuestThrowException(null, null, null, site);

        var type = _model.GetTypeInfo(creation).Type as INamedTypeSymbol;
        return new GuestThrowException(type, null, ParamNameResolver.Resolve(creation, _model), site);
    }

    /// <summary>
    /// 組み込みの演算子（int, long, double, decimal, bool, string）を求める
    /// </summary>
    /// <param name="expression">演算の式</param>
    /// <returns>演算子のシンボル</returns>
    private IMethodSymbol GetOperator(ExpressionSyntax expression)
    {
        if (_model.GetSymbolInfo(expression).Symbol is IMethodSymbol op
            && (op.MethodKind == MethodKind.BuiltinOperator
                || op.ContainingType.SpecialType is SpecialType.System_String or SpecialType.System_Decimal
                || TemporalValues.KindOf(op.ContainingType) != TemporalKind.None))
        {
            return op;
        }

        throw new UnsupportedSyntaxException(Messages.Get("UnsupportedOperator", Shorten(expression)));
    }

    /// <summary>
    /// フィールド・自動実装プロパティへの代入（=, +=, -= など）を、インスタンスの状態に書く
    /// </summary>
    /// <param name="assignment">代入式</param>
    /// <param name="target">フィールド・自動実装プロパティ</param>
    private void AssignState(AssignmentExpressionSyntax assignment, ISymbol target)
    {
        var type = target is IFieldSymbol field ? field.Type : ((IPropertySymbol)target).Type;
        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            _state!.Set(target, EvaluateConverted(assignment.Right));
            return;
        }

        if (!_state!.TryGet(target, out var current)) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", target.Name));

        var op = GetOperator(assignment);
        var left = ClrOperations.Convert(current, op.Parameters[0].Type);
        var right = ClrOperations.Convert(EvaluateConverted(assignment.Right), op.Parameters[1].Type);
        _state.Set(target, ClrOperations.Convert(ClrOperations.Binary(assignment.Kind(), op.Parameters[0].Type, left, right), type));
    }

    /// <summary>
    /// テスト対象のインスタンスを作った直後の状態を、フィールドの初期化子とコンストラクタの本体を評価して求める。
    /// 評価できない文で代入しているフィールドは、不明にする
    /// </summary>
    /// <param name="type">テスト対象のクラス</param>
    /// <param name="constructor">テストで使うコンストラクタ</param>
    /// <param name="arguments">コンストラクタに渡す値（評価用の値。モックなどは InstanceState.Unknown）</param>
    /// <param name="compilation">コンパイル</param>
    /// <returns>状態</returns>
    public static InstanceState CreateState(INamedTypeSymbol type, IMethodSymbol constructor, IReadOnlyList<object?> arguments, Compilation compilation)
    {
        var state = InstanceState.CreateInitial(type);
        var budget = new StepBudget();
        foreach (var member in type.GetMembers().Where(InstanceState.IsStateMember))
        {
            state.Set(member, InitialValue(member, constructor, state, compilation, budget));
        }

        if (constructor.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault() is not ConstructorDeclarationSyntax declaration) return state;

        // this(...) で別のコンストラクタを呼ぶ場合は、その順序を再現しないため、値をすべて不明にする
        if (declaration.Initializer is { } initializer && initializer.IsKind(SyntaxKind.ThisConstructorInitializer))
        {
            state.ForgetAll();
            return state;
        }

        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        var interpreter = new MethodInterpreter(model, constructor, ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default), budget) { _state = state };
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[constructor.Parameters[i]] = ToInterpreterValue(arguments[i]);

        // 式本体（=> _x = x;）は1つの式文として扱う
        IEnumerable<SyntaxNode> steps = declaration.ExpressionBody is { } arrow ? [arrow.Expression] : declaration.Body?.Statements ?? [];
        foreach (var step in steps)
        {
            if (!interpreter.TryRunConstructorStep(step)) break;
        }

        return state;
    }

    /// <summary>
    /// コンストラクタの文（または式本体）を1つ実行する。評価できない文で代入しているフィールドは不明にし、
    /// 例外になった場合は、すべての値を不明にして打ち切る
    /// </summary>
    /// <param name="step">文、または式本体の式</param>
    /// <returns>続けて次の文を実行してよいならtrue</returns>
    private bool TryRunConstructorStep(SyntaxNode step)
    {
        try
        {
            if (step is StatementSyntax statement) Execute(statement);
            else ExecuteExpressionStatement((ExpressionSyntax)step);
            return true;
        }
        catch (UnsupportedSyntaxException)
        {
            foreach (var assigned in step.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
            {
                var target = _model.GetSymbolInfo(assigned.Left).Symbol;
                if (target is not null && _state!.Contains(target)) _state.Set(target, InstanceState.Unknown);
            }

            return true;
        }
        catch (Exception ex) when (ex is GuestThrowException or ArithmeticException)
        {
            _state!.ForgetAll();
            return false;
        }
    }

    /// <summary>
    /// フィールド・自動実装プロパティの初期値（初期化子を評価した値、なければ型の既定値）を求める
    /// </summary>
    /// <param name="member">フィールド・自動実装プロパティ</param>
    /// <param name="constructor">テストで使うコンストラクタ（初期化子を評価するときの文脈）</param>
    /// <param name="state">ここまでに求めた状態</param>
    /// <param name="compilation">コンパイル</param>
    /// <param name="budget">計算量の上限</param>
    /// <returns>初期値（評価できなければ不明）</returns>
    private static object? InitialValue(ISymbol member, IMethodSymbol constructor, InstanceState state, Compilation compilation, StepBudget budget)
    {
        var syntax = member.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        var initializer = syntax switch
        {
            VariableDeclaratorSyntax variable => variable.Initializer?.Value,
            PropertyDeclarationSyntax property => property.Initializer?.Value,
            _ => null,
        };
        var type = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
        if (initializer is null) return InstanceState.DefaultValue(type);

        var interpreter = new MethodInterpreter(compilation.GetSemanticModel(initializer.SyntaxTree), constructor, ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default), budget)
        {
            _state = state,
        };
        try
        {
            return interpreter.EvaluateConverted(initializer);
        }
        catch (Exception ex) when (ex is UnsupportedSyntaxException or GuestThrowException or ArithmeticException)
        {
            return InstanceState.Unknown;
        }
    }

    // ループ1つで繰り返す回数の上限（IDE の解析の中で、終わらないループを回し続けないようにする）
    private const int MaxIterations = 10_000;

    /// <summary>
    /// ループ（foreach・for・while・do while）を実行する
    /// </summary>
    /// <param name="loop">ループの文</param>
    /// <returns>実行結果</returns>
    private Flow ExecuteLoop(StatementSyntax loop)
    {
        return loop switch
        {
            ForEachStatementSyntax forEach => ExecuteForEach(forEach),
            ForStatementSyntax forLoop => ExecuteFor(forLoop),
            WhileStatementSyntax whileLoop => ExecuteWhile(whileLoop.Condition, whileLoop.Statement, checkFirst: true),
            DoStatementSyntax doLoop => ExecuteWhile(doLoop.Condition, doLoop.Statement, checkFirst: false),
            _ => throw new UnsupportedSyntaxException(DescribeStatement(loop)),
        };
    }

    /// <summary>
    /// foreach を実行する（List・配列・文字列を順に回す）
    /// </summary>
    /// <param name="loop">foreach 文</param>
    /// <returns>実行結果</returns>
    private Flow ExecuteForEach(ForEachStatementSyntax loop)
    {
        var variable = _model.GetDeclaredSymbol(loop) ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedLoop"));
        foreach (var item in CollectionMethods.Enumerate(EvaluateConverted(loop.Expression), loop))
        {
            _variables[variable] = item;
            var flow = Execute(loop.Statement);
            if (flow == Flow.Break) break;
            if (flow == Flow.Return) return flow;
        }

        return Flow.Normal;
    }

    /// <summary>
    /// for を実行する
    /// </summary>
    /// <param name="loop">for 文</param>
    /// <returns>実行結果</returns>
    private Flow ExecuteFor(ForStatementSyntax loop)
    {
        if (loop.Declaration is { } declaration) DeclareVariables(declaration);
        foreach (var initializer in loop.Initializers) ExecuteExpressionStatement(initializer);

        for (var count = 0; ; count++)
        {
            if (MaxIterations < count) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTooManySteps"));
            if (loop.Condition is not null && !ToBoolean(EvaluateConverted(loop.Condition))) break;

            var flow = Execute(loop.Statement);
            if (flow == Flow.Break) break;
            if (flow == Flow.Return) return flow;
            foreach (var incrementor in loop.Incrementors) ExecuteExpressionStatement(incrementor);
        }

        return Flow.Normal;
    }

    /// <summary>
    /// while・do while を実行する
    /// </summary>
    /// <param name="condition">条件</param>
    /// <param name="body">繰り返す文</param>
    /// <param name="checkFirst">先に条件を見るか（while なら true、do while なら false）</param>
    /// <returns>実行結果</returns>
    private Flow ExecuteWhile(ExpressionSyntax condition, StatementSyntax body, bool checkFirst)
    {
        for (var count = 0; ; count++)
        {
            if (MaxIterations < count) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTooManySteps"));
            if ((checkFirst || 0 < count) && !ToBoolean(EvaluateConverted(condition))) break;

            var flow = Execute(body);
            if (flow == Flow.Break) break;
            if (flow == Flow.Return) return flow;
        }

        return Flow.Normal;
    }

    /// <summary>
    /// i++・--i などを実行する（ローカル変数・引数・フィールド）
    /// </summary>
    /// <param name="operand">増減する変数</param>
    /// <param name="increment">増やすなら true、減らすなら false</param>
    private void Increment(ExpressionSyntax operand, bool increment)
    {
        var target = _model.GetSymbolInfo(operand).Symbol;
        var type = target switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpressionStatement", Shorten(operand))),
        };
        var current = EvaluateConverted(operand);
        var result = ClrOperations.Binary(
            increment ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression, type, current, ClrOperations.Convert(1, type));
        if (target is ILocalSymbol or IParameterSymbol && _variables.ContainsKey(target)) _variables[target] = result;
        else if (_state is not null && _state.Contains(target!)) _state.Set(target!, result);
        else throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldAssignment", Shorten(operand)));
    }

    /// <summary>
    /// 呼び出しを評価する（同じクラスの関数、LINQ、List のメソッド、そのほかの標準のメソッドの順に試す）
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    /// <returns>戻り値</returns>
    private object? EvaluateInvocation(InvocationExpressionSyntax invocation)
    {
        if (SameClassCall.Resolve(invocation, _model) is { } call) return Call(call);

        if (_model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && invocation.Expression is MemberAccessExpressionSyntax receiver)
        {
            var arguments = invocation.ArgumentList.Arguments.Select(a => a.Expression).ToList();
            if (method.ReducedFrom is not null && CollectionMethods.TryInvokeLinq(
                method, EvaluateConverted(receiver.Expression), arguments, EvaluateConverted, InvokeLambda, invocation, out var linq))
            {
                return linq;
            }

            if (!method.IsStatic && IsListType(method.ContainingType))
            {
                var target = EvaluateConverted(receiver.Expression);
                if (target is null) throw new GuestThrowException(null, "System.NullReferenceException", null, invocation);
                if (target is ListValue list && CollectionMethods.TryInvokeList(list, method.Name, arguments.Select(EvaluateConverted).ToList(), invocation, out var value))
                {
                    return value;
                }

                throw new UnsupportedSyntaxException(Messages.Get("UnsupportedMethodCall", invocation.Expression));
            }
        }

        return KnownMethods.Invoke(invocation, _model, EvaluateConverted);
    }

    /// <summary>
    /// ラムダ式（x => x.Amount など、引数1つのもの）を、引数の値で評価する
    /// </summary>
    /// <param name="expression">ラムダ式</param>
    /// <param name="argument">引数の値</param>
    /// <returns>ラムダ式の値</returns>
    private object? InvokeLambda(ExpressionSyntax expression, object? argument)
    {
        var parameter = expression switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.ParameterList.Parameters[0],
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpression", Shorten(expression))),
        };
        var lambda = (LambdaExpressionSyntax)expression;
        var symbol = _model.GetDeclaredSymbol(parameter) ?? throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpression", Shorten(expression)));
        var hadValue = _variables.TryGetValue(symbol, out var previous);
        _variables[symbol] = argument;
        try
        {
            if (lambda.ExpressionBody is { } body) return EvaluateConverted(body);

            var savedReturn = _returnValue;
            var flow = Execute(lambda.Block!);
            var result = _returnValue;
            _returnValue = savedReturn;
            return flow == Flow.Return ? result : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNoReturnPath"));
        }
        finally
        {
            if (hadValue) _variables[symbol] = previous;
            else _variables.Remove(symbol);
        }
    }

    /// <summary>
    /// 添字で要素を読む（list[i]・array[i]・text[i]）
    /// </summary>
    /// <param name="access">要素アクセス式</param>
    /// <returns>要素</returns>
    private object? ReadElement(ElementAccessExpressionSyntax access)
    {
        if (access.ArgumentList.Arguments.Count != 1) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpression", Shorten(access)));

        var receiver = EvaluateConverted(access.Expression);
        var index = EvaluateConverted(access.ArgumentList.Arguments[0].Expression);
        return receiver switch
        {
            null => throw new GuestThrowException(null, "System.NullReferenceException", null, access),
            ListValue list => CollectionMethods.GetItem(list, index, access),
            string text when index is int i => 0 <= i && i < text.Length ? text[i] : throw new GuestThrowException(null, "System.IndexOutOfRangeException", null, access),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedExpression", Shorten(access))),
        };
    }

    /// <summary>
    /// 添字で要素に代入する（list[i] = x、list[i] += x）
    /// </summary>
    /// <param name="assignment">代入式</param>
    /// <param name="access">要素アクセス式</param>
    private void AssignElement(AssignmentExpressionSyntax assignment, ElementAccessExpressionSyntax access)
    {
        if (access.ArgumentList.Arguments.Count != 1 || EvaluateConverted(access.Expression) is not ListValue list)
        {
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldAssignment", Shorten(assignment)));
        }

        var index = EvaluateConverted(access.ArgumentList.Arguments[0].Expression);
        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            CollectionMethods.SetItem(list, index, EvaluateConverted(assignment.Right), access);
            return;
        }

        var op = GetOperator(assignment);
        var left = ClrOperations.Convert(CollectionMethods.GetItem(list, index, access), op.Parameters[0].Type);
        var right = ClrOperations.Convert(EvaluateConverted(assignment.Right), op.Parameters[1].Type);
        var type = _model.GetTypeInfo(access).Type!;
        CollectionMethods.SetItem(list, index, ClrOperations.Convert(ClrOperations.Binary(assignment.Kind(), op.Parameters[0].Type, left, right), type), access);
    }

    /// <summary>
    /// new List&lt;T&gt;()・new List&lt;T&gt; { a, b }・new List&lt;T&gt;(ほかのコレクション) を評価する
    /// </summary>
    /// <param name="creation">生成の式</param>
    /// <returns>List の値</returns>
    private ListValue CreateList(BaseObjectCreationExpressionSyntax creation)
    {
        var elementType = CollectionMethods.ElementTypeOf(_model.GetTypeInfo(creation).Type);
        var items = new List<object?>();
        var arguments = creation.ArgumentList?.Arguments ?? default;
        if (arguments.Count == 1 && _model.GetTypeInfo(arguments[0].Expression).Type?.SpecialType != SpecialType.System_Int32)
        {
            items.AddRange(CollectionMethods.Enumerate(EvaluateConverted(arguments[0].Expression), creation));
        }
        else if (1 < arguments.Count)
        {
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCreation", Shorten(creation)));
        }

        if (creation.Initializer is { } initializer) items.AddRange(initializer.Expressions.Select(EvaluateConverted));
        return new ListValue(elementType, items);
    }

    /// <summary>
    /// 配列・コレクション式（new[] { a, b }、new int[3]、[a, b]）を評価する
    /// </summary>
    /// <param name="expression">式</param>
    /// <returns>List・配列の値</returns>
    private ListValue CreateCollection(ExpressionSyntax expression)
    {
        var type = _model.GetTypeInfo(expression).ConvertedType ?? _model.GetTypeInfo(expression).Type;
        var elementType = CollectionMethods.ElementTypeOf(type);
        var isArray = type is IArrayTypeSymbol;
        switch (expression)
        {
            case ArrayCreationExpressionSyntax { Initializer: { } initializer }:
                return new ListValue(elementType, initializer.Expressions.Select(EvaluateConverted), isArray: true);
            case ArrayCreationExpressionSyntax sized when elementType is not null && sized.Type.RankSpecifiers.Count == 1 && sized.Type.RankSpecifiers[0].Sizes.Count == 1:
                var length = EvaluateConverted(sized.Type.RankSpecifiers[0].Sizes[0]) is int n && 0 <= n && n <= 10_000 ? n : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCreation", Shorten(expression)));
                var defaultValue = InstanceState.DefaultValue(elementType);
                if (ReferenceEquals(defaultValue, InstanceState.Unknown)) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCreation", Shorten(expression)));
                return new ListValue(elementType, Enumerable.Repeat(defaultValue, length), isArray: true);
            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return new ListValue(elementType, implicitArray.Initializer.Expressions.Select(EvaluateConverted), isArray: true);
            case CollectionExpressionSyntax collection when collection.Elements.All(e => e is ExpressionElementSyntax):
                return new ListValue(elementType, collection.Elements.Cast<ExpressionElementSyntax>().Select(e => EvaluateConverted(e.Expression)), isArray);
            default:
                throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCreation", Shorten(expression)));
        }
    }

    /// <summary>
    /// List&lt;T&gt; そのものかを判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>List&lt;T&gt; ならtrue</returns>
    private static bool IsList(ITypeSymbol? type)
    {
        return type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";
    }

    /// <summary>
    /// List のメソッドを持つ型（List&lt;T&gt;・IList&lt;T&gt;・ICollection&lt;T&gt;）かを判定する
    /// </summary>
    /// <param name="type">メソッドを持つ型</param>
    /// <returns>List のメソッドを持つならtrue</returns>
    private static bool IsListType(ITypeSymbol type)
    {
        return type.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.ICollection<T>";
    }

    /// <summary>
    /// 入力の値を評価用の値にする（列挙型のメンバーは、元になる整数にして比較・switch で扱えるようにする）
    /// </summary>
    /// <param name="value">入力の値</param>
    /// <returns>評価用の値</returns>
    private static object? ToInterpreterValue(object? value)
    {
        if (value is CollectionValue collection)
        {
            // 入力の値は行どうしで共有されるため、書き換えても影響しないよう新しい List にする
            return new ListValue(collection.ElementType, collection.Items.Select(ToInterpreterValue), collection.CollectionType is IArrayTypeSymbol);
        }

        if (value is not EnumMemberValue member) return value;

        var constant = member.Type.GetMembers(member.Name).OfType<IFieldSymbol>().First().ConstantValue;
        // 三項演算子の両辺を object にして、int の値が long に揃えられないようにする
        return constant is long l ? (object)l : System.Convert.ToInt32(constant, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 値を bool として取り出す
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>bool の値</returns>
    private static bool ToBoolean(object? value)
    {
        return value is bool b ? b : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNonBoolCondition"));
    }

    /// <summary>
    /// 値をガード条件の判定用の値にする
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>判定用の値</returns>
    private static TestValue ToTestValue(object? value)
    {
        return value switch
        {
            null => TestValue.Null,
            string s => TestValue.FromString(s),
            _ when ClrOperations.IsNumber(value) => TestValue.FromNumber(System.Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => TestValue.False,
        };
    }

    /// <summary>
    /// 対象外の文を、レポート用に分かりやすく言い換える
    /// </summary>
    /// <param name="statement">文</param>
    /// <returns>説明</returns>
    private static string DescribeStatement(StatementSyntax statement)
    {
        return statement switch
        {
            ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax => Messages.Get("UnsupportedLoop"),
            TryStatementSyntax => Messages.Get("UnsupportedTry"),
            UsingStatementSyntax => Messages.Get("UnsupportedUsing"),
            _ => Messages.Get("UnsupportedStatement", Shorten(statement)),
        };
    }

    /// <summary>
    /// レポート用に構文を1行・短めにする
    /// </summary>
    /// <param name="node">構文</param>
    /// <returns>短くした文字列</returns>
    private static string Shorten(SyntaxNode node)
    {
        const int MaxLength = 60;
        var text = string.Join(" ", node.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return text.Length <= MaxLength ? text : text.Substring(0, MaxLength) + "…";
    }

    /// <summary>
    /// 1回の評価で計算できる式の数の上限（同じクラスの関数の呼び出しが多いと計算量が爆発し、IDE の解析が終わらなくなるため）
    /// </summary>
    private sealed class StepBudget
    {
        private const int MaxSteps = 100_000;
        private int _used;

        /// <summary>
        /// 式を1つ計算した分を数え、上限を超えたら評価をやめる
        /// </summary>
        public void Consume()
        {
            if (MaxSteps < ++_used) throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTooManySteps"));
            EnsureStack();
        }

        /// <summary>
        /// 残りのスタックが少なければ評価をやめる（入れ子の深い式・呼び出しで、スタックオーバーフローにより IDE の解析のプロセスごと落ちないようにする。
        /// スタックオーバーフローは例外として捕まえられないため、その手前で止める）
        /// </summary>
        public static void EnsureStack()
        {
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
            }
            catch (InsufficientExecutionStackException)
            {
                throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTooDeep"));
            }
        }
    }
}
