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
    private object? _returnValue;

    private MethodInterpreter(SemanticModel model, IMethodSymbol member)
    {
        _model = model;
        _member = member;
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
        var interpreter = new MethodInterpreter(model, member);
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
    /// 式本体またはブロック本体を評価する
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <returns>評価結果</returns>
    private EvaluationResult Run(BaseMethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is { } arrow) return EvaluationResult.Returned(EvaluateConverted(arrow.Expression));
        if (declaration.Body is null) return EvaluationResult.Unsupported("本体がありません");

        return Execute(declaration.Body) == Flow.Return
            ? EvaluationResult.Returned(_returnValue)
            : EvaluationResult.Unsupported("値を返さずに終了する経路があります");
    }

    /// <summary>
    /// 文を実行する
    /// </summary>
    /// <param name="statement">文</param>
    /// <returns>実行結果</returns>
    private Flow Execute(StatementSyntax statement)
    {
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
                if (!constant.HasValue) throw new UnsupportedSyntaxException($"定数でない case '{caseLabel.Value}'");
                return ClrOperations.ValuesEqual(value, constant.Value);
            case CasePatternSwitchLabelSyntax patternLabel:
                if (patternLabel.WhenClause is not null) throw new UnsupportedSyntaxException("switch 文の when 句");
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
            if (variable.Initializer is null) throw new UnsupportedSyntaxException($"初期値のないローカル変数 '{variable.Identifier.ValueText}'");

            var symbol = _model.GetDeclaredSymbol(variable) ?? throw new UnsupportedSyntaxException($"ローカル変数 '{variable.Identifier.ValueText}'");
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
            case InvocationExpressionSyntax invocation:
                ExecuteGuardHelper(invocation);
                return;
            case AssignmentExpressionSyntax assignment:
                Assign(assignment);
                return;
            default:
                throw new UnsupportedSyntaxException($"式文 '{Shorten(expression)}'");
        }
    }

    /// <summary>
    /// ArgumentNullException.ThrowIfNull などのガードメソッドを評価する
    /// </summary>
    /// <param name="invocation">呼び出し式</param>
    private void ExecuteGuardHelper(InvocationExpressionSyntax invocation)
    {
        var guard = ThrowHelperParser.TryParse(invocation, _model, _member);
        if (guard is null || !guard.IsRecognized) throw new UnsupportedSyntaxException($"メソッド呼び出し '{Shorten(invocation)}'");

        foreach (var guardCase in guard.Cases)
        {
            if (guardCase.Condition.IsViolatedBy(ToTestValue(_variables[guardCase.Parameter])))
            {
                throw new GuestThrowException(guardCase.ExceptionType, null, guardCase.ParamName, invocation);
            }
        }
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
            throw new UnsupportedSyntaxException($"フィールド・プロパティへの代入 '{Shorten(assignment)}'");
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
            InvocationExpressionSyntax invocation => KnownMethods.Invoke(invocation, _model, EvaluateConverted),
            MemberAccessExpressionSyntax memberAccess => KnownMethods.ReadMember(memberAccess, _model, EvaluateConverted),
            SwitchExpressionSyntax switchExpression => EvaluateSwitch(switchExpression),
            IsPatternExpressionSyntax isPattern => new PatternMatcher(_model).Matches(isPattern.Pattern, EvaluateConverted(isPattern.Expression)),
            ThrowExpressionSyntax throwExpression => throw CreateThrow(throwExpression.Expression, throwExpression),
            InterpolatedStringExpressionSyntax => throw new UnsupportedSyntaxException("文字列補間（カルチャに依存するため）"),
            _ => throw new UnsupportedSyntaxException($"式 '{Shorten(expression)}'"),
        };
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

        throw new UnsupportedSyntaxException($"フィールド・プロパティ '{identifier.Identifier.ValueText}' の参照");
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
            if (arm.WhenClause is not null) throw new UnsupportedSyntaxException("switch 式の when 句");
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
                || op.ContainingType.SpecialType is SpecialType.System_String or SpecialType.System_Decimal))
        {
            return op;
        }

        throw new UnsupportedSyntaxException($"演算子 '{Shorten(expression)}'（ユーザー定義の演算子など）");
    }

    /// <summary>
    /// 値を bool として取り出す
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>bool の値</returns>
    private static bool ToBoolean(object? value)
    {
        return value is bool b ? b : throw new UnsupportedSyntaxException("bool 以外の条件");
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
            ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax => "ループ",
            TryStatementSyntax => "try 文",
            UsingStatementSyntax => "using 文",
            _ => $"文 '{Shorten(statement)}'",
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
}
