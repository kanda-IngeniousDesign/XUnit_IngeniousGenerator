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

        /// <summary>switch 文の break に到達した</summary>
        Break,
    }

    /// <summary>
    /// メソッドを指定の引数で評価する
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッドのシンボル</param>
    /// <param name="arguments">引数の値（引数の順）</param>
    /// <returns>評価結果</returns>
    public static EvaluationResult Evaluate(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member, IReadOnlyList<object?> arguments)
    {
        var interpreter = new MethodInterpreter(model, member, ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default), new StepBudget());
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[member.Parameters[i]] = arguments[i];

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
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[member.Parameters[i]] = arguments[i];

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
        for (var i = 0; i < arguments.Count; i++) interpreter._variables[member.Parameters[i]] = arguments[i];

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
            if (flow == Flow.Return) return Flow.Return;
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
        foreach (var variable in local.Declaration.Variables)
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
            case InvocationExpressionSyntax invocation:
                ExecuteGuardHelper(invocation);
                return;
            case AssignmentExpressionSyntax assignment:
                Assign(assignment);
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
        var target = _model.GetSymbolInfo(assignment.Left).Symbol;
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
            InvocationExpressionSyntax invocation => SameClassCall.Resolve(invocation, _model) is { } call
                ? Call(call)
                : KnownMethods.Invoke(invocation, _model, EvaluateConverted),
            MemberAccessExpressionSyntax memberAccess => KnownMethods.ReadMember(memberAccess, _model, EvaluateConverted),
            BaseObjectCreationExpressionSyntax creation when TemporalValues.KindOf(_model.GetTypeInfo(creation).Type) != TemporalKind.None =>
                TemporalMethods.Create(creation, _model, EvaluateConverted),
            SwitchExpressionSyntax switchExpression => EvaluateSwitch(switchExpression),
            IsPatternExpressionSyntax isPattern => new PatternMatcher(_model).Matches(isPattern.Pattern, EvaluateConverted(isPattern.Expression)),
            ThrowExpressionSyntax throwExpression => throw CreateThrow(throwExpression.Expression, throwExpression),
            InterpolatedStringExpressionSyntax => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedInterpolation")),
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

        var callee = new MethodInterpreter(call.Model, call.Method, _callStack, _budget);
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
        if (symbol is not null && _variables.TryGetValue(symbol, out var value)) return value;

        throw new UnsupportedSyntaxException(Messages.Get("UnsupportedFieldReference", identifier.Identifier.ValueText));
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
        var left = ClrOperations.Convert(EvaluateConverted(binary.Left), op.Parameters[0].Type);
        var right = ClrOperations.Convert(EvaluateConverted(binary.Right), op.Parameters[1].Type);
        return ClrOperations.Binary(binary.Kind(), op.Parameters[0].Type, left, right);
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
