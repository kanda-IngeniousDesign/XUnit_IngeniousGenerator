using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// メソッド本体の比較・パターンから境界値を読み取り、戻り値テストの入力を作る
/// </summary>
internal static class BoundaryInputGenerator
{
    private const int MaxRows = 40;

    /// <summary>
    /// 数値・文字列・bool・列挙型・コレクションの引数は基準の値と境界値の組み合わせを、それ以外の引数は固定の値（モックなど）を作る
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="guards">先頭のガード</param>
    /// <param name="allowMocks">インターフェース・抽象クラスを Moq でモックしてよいか</param>
    /// <param name="varyCollections">コレクションを空・null にも変えるか（スナップショットでは記録ファイル名が重なるため変えない）</param>
    /// <param name="failedParameter">値を作れなかった引数</param>
    /// <param name="avoidBodyThrows">本体の途中で例外になる値も避けるか（false なら先頭のガードだけを避け、途中の throw に到達する入力も作る）</param>
    /// <param name="construct">引数なしコンストラクタがないクラスの引数を生成する方法</param>
    /// <returns>入力（値を作れない引数があればnull）</returns>
    public static BoundaryInputs? Generate(
        IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, MemberGuards guards, bool allowMocks, bool varyCollections,
        out IParameterSymbol? failedParameter, bool avoidBodyThrows = true, Func<INamedTypeSymbol, TestValue?>? construct = null)
    {
        // 先頭のガードに加えて、本体の途中で例外になる条件も避ける（スナップショットは生成時に実行して確かめられないため）
        var leadingGuards = guards.Statements.Where(s => s.IsRecognized).SelectMany(s => s.Cases);
        var guardConditions = (avoidBodyThrows ? leadingGuards.Concat(GuardExtractor.CollectThrowConditions(declaration, model, member)) : leadingGuards)
            .ToList();
        var boundaries = CollectBoundaryConstants(declaration, model, member);

        var builder = new InputsBuilder();
        foreach (var parameter in member.Parameters)
        {
            failedParameter = parameter;
            var conditions = guardConditions
                .Where(c => SymbolEqualityComparer.Default.Equals(c.Parameter, parameter))
                .Select(c => c.Condition)
                .ToList();
            boundaries.TryGetValue(parameter, out var constants);
            if (!TryAddParameter(builder, parameter, conditions, constants ?? [], allowMocks, varyCollections, construct)) return null;
        }

        // 引数どうしの比較（a > b など）は、片方を「もう片方の基準の値 ±1」にした入力で分岐を通す
        foreach (var (left, right) in CollectParameterRelations(declaration, model, member))
        {
            builder.AddRelativeVariations(left, right);
            builder.AddRelativeVariations(right, left);
        }

        failedParameter = null;
        return new BoundaryInputs(builder.DataParameters, builder.FixedValues, CombineOneAtATime([.. builder.BaseRow], [.. builder.Variations]));
    }

    /// <summary>
    /// 本体で、数値の引数どうしを比べている組を集める
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド</param>
    /// <returns>比べている引数の組</returns>
    private static List<(IParameterSymbol Left, IParameterSymbol Right)> CollectParameterRelations(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member)
    {
        var relations = new List<(IParameterSymbol, IParameterSymbol)>();
        foreach (var binary in declaration.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(IsComparison))
        {
            var left = ConditionParser.GetParameter(binary.Left, model, member);
            var right = ConditionParser.GetParameter(binary.Right, model, member);
            if (left is null || right is null || SymbolEqualityComparer.Default.Equals(left, right)) continue;
            if (!ConditionParser.IsNumeric(left.Type) || !ConditionParser.IsNumeric(right.Type)) continue;

            relations.Add((left, right));
        }

        return relations;
    }

    /// <summary>
    /// 引数1つ分の基準の値と境界値（または固定の値）を加える
    /// </summary>
    /// <param name="builder">入力の組み立て先</param>
    /// <param name="parameter">引数</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <param name="constants">本体でその引数と比べている定数</param>
    /// <param name="allowMocks">インターフェース・抽象クラスを Moq でモックしてよいか</param>
    /// <param name="varyCollections">コレクションを空・null にも変えるか</param>
    /// <param name="construct">引数なしコンストラクタがないクラスの引数を生成する方法</param>
    /// <returns>値を作れたらtrue</returns>
    private static bool TryAddParameter(
        InputsBuilder builder, IParameterSymbol parameter, List<GuardCondition> conditions, List<object> constants, bool allowMocks, bool varyCollections,
        Func<INamedTypeSymbol, TestValue?>? construct)
    {
        var allowNull = AllowsNull(parameter, conditions);
        if (DataTypes.IsEnum(parameter.Type) || DataTypes.TryGetCollectionElement(parameter.Type, out _))
        {
            var (baseValue, others) = GetStructuredValues(parameter.Type, allowNull, varyCollections);
            builder.AddData(parameter, baseValue, others);
            return true;
        }

        if (!DataTypes.IsPrimitive(parameter.Type))
        {
            var fixedValue = FindFixedValue(parameter.Type, conditions, allowMocks, construct);
            if (fixedValue is null) return false;
            builder.FixedValues[parameter] = fixedValue;
            return true;
        }

        var valid = FindBaseValue(parameter.Type, conditions);
        if (valid is null) return false;

        var variations = GetParameterVariations(parameter.Type, constants, conditions);
        if (allowNull && parameter.Type.SpecialType == SpecialType.System_String) variations.Add(null);
        builder.AddData(parameter, ToClrValue(valid, parameter.Type), variations, conditions);
        return true;
    }

    /// <summary>
    /// 入力の組み立て途中の状態（境界値を変える引数の基準の値・ほかの値と、固定の値）
    /// </summary>
    private sealed class InputsBuilder
    {
        /// <summary>境界値を変える引数</summary>
        public List<IParameterSymbol> DataParameters { get; } = [];

        /// <summary>固定で渡す引数の値</summary>
        public Dictionary<IParameterSymbol, TestValue> FixedValues { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>基準の入力</summary>
        public List<object?> BaseRow { get; } = [];

        /// <summary>引数ごとのほかの値</summary>
        public List<List<object?>> Variations { get; } = [];

        /// <summary>
        /// 境界値を変える引数を加える
        /// </summary>
        /// <param name="parameter">引数</param>
        /// <param name="baseValue">基準の値</param>
        /// <param name="others">ほかの値</param>
        public void AddData(IParameterSymbol parameter, object? baseValue, List<object?> others)
        {
            AddData(parameter, baseValue, others, []);
        }

        /// <summary>
        /// 境界値を変える引数を、ガード条件とともに加える
        /// </summary>
        /// <param name="parameter">引数</param>
        /// <param name="baseValue">基準の値</param>
        /// <param name="others">ほかの値</param>
        /// <param name="conditions">その引数のガード条件（関係による境界値を加えるときに使う）</param>
        public void AddData(IParameterSymbol parameter, object? baseValue, List<object?> others, List<GuardCondition> conditions)
        {
            DataParameters.Add(parameter);
            BaseRow.Add(baseValue);
            Variations.Add(others);
            _conditions.Add(conditions);
        }

        private readonly List<List<GuardCondition>> _conditions = [];

        /// <summary>
        /// target の境界値に「other の基準の値 -1, ±0, +1」を加える（ガードで例外になる値・型に収まらない値は除く）
        /// </summary>
        /// <param name="target">境界値を加える引数</param>
        /// <param name="other">比べている相手の引数</param>
        public void AddRelativeVariations(IParameterSymbol target, IParameterSymbol other)
        {
            var targetIndex = DataParameters.FindIndex(p => SymbolEqualityComparer.Default.Equals(p, target));
            var otherIndex = DataParameters.FindIndex(p => SymbolEqualityComparer.Default.Equals(p, other));
            if (targetIndex < 0 || otherIndex < 0 || !ClrOperationsBridge.TryToDecimal(BaseRow[otherIndex], out var pivot)) return;

            foreach (var candidate in new[] { pivot - 1, pivot, pivot + 1 }.Select(TestValue.FromNumber))
            {
                if (!ValueSolver.IsRepresentable(candidate, target.Type) || _conditions[targetIndex].Any(c => c.IsViolatedBy(candidate))) continue;

                var value = ToClrValue(candidate, target.Type);
                if (!Variations[targetIndex].Contains(value)) Variations[targetIndex].Add(value);
            }
        }
    }

    /// <summary>
    /// null を渡してよい引数か（null 許容の注釈 ? が付いていて、null で例外になるガードがない）を判定する
    /// </summary>
    /// <param name="parameter">引数</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <returns>null を渡してよいならtrue</returns>
    private static bool AllowsNull(IParameterSymbol parameter, List<GuardCondition> conditions)
    {
        return parameter.Type.IsReferenceType
            && parameter.NullableAnnotation == NullableAnnotation.Annotated
            && !conditions.Any(c => c.Kind == GuardConditionKind.Null);
    }

    /// <summary>
    /// 列挙型（全メンバー）・コレクション（要素1件を基準に、空・null）の値を作る
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="allowNull">null を渡してよいか</param>
    /// <param name="varyCollections">コレクションを空・null にも変えるか</param>
    /// <returns>基準の値と、ほかの値</returns>
    private static (object? Base, List<object?> Others) GetStructuredValues(ITypeSymbol type, bool allowNull, bool varyCollections)
    {
        if (DataTypes.IsEnum(type))
        {
            var members = GetEnumMembers(type);
            return (members.FirstOrDefault(), members.Skip(1).ToList());
        }

        DataTypes.TryGetCollectionElement(type, out var elementType);
        var element = DataTypes.IsEnum(elementType)
            ? GetEnumMembers(elementType).FirstOrDefault()
            : ToClrValue(FindBaseValue(elementType, []) ?? TestValue.FromString("a"), elementType);
        var one = new CollectionValue(type, elementType, [element]);
        if (!varyCollections) return (one, []);

        var others = new List<object?> { new CollectionValue(type, elementType, []) };
        if (allowNull) others.Add(null);
        return (one, others);
    }

    /// <summary>
    /// 列挙型のメンバーを宣言順に返す
    /// </summary>
    /// <param name="type">列挙型</param>
    /// <returns>メンバー</returns>
    private static List<object?> GetEnumMembers(ITypeSymbol type)
    {
        return type.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => (object?)new EnumMemberValue(type, f.Name))
            .ToList();
    }

    /// <summary>
    /// 境界値を変えない引数（クラス・インターフェースなど）に渡す固定の値を選ぶ
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <param name="allowMocks">インターフェース・抽象クラスを Moq でモックしてよいか</param>
    /// <param name="construct">引数なしコンストラクタがないクラスを生成する方法</param>
    /// <returns>固定の値（作れなければnull）</returns>
    private static TestValue? FindFixedValue(ITypeSymbol type, List<GuardCondition> conditions, bool allowMocks, Func<INamedTypeSymbol, TestValue?>? construct)
    {
        // null を渡すと本体で NullReferenceException になりやすいため、null しか作れない引数は対象外にする
        var value = ValueSolver.FindValid(type, conditions, allowMocks, construct);
        return value is null || value.Kind == TestValueKind.Null ? null : value;
    }

    /// <summary>
    /// 本体で比べている定数と、ガードの定数から、引数の境界値を作る
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="constants">本体でその引数と比べている定数</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <returns>境界値の候補</returns>
    private static List<object?> GetParameterVariations(ITypeSymbol type, List<object> constants, List<GuardCondition> conditions)
    {
        // ThrowIfGreaterThan(value, 100) などのガードの定数も、通過する側の境界（100 など）として使う
        var guardConstants = conditions.Where(c => c.Kind == GuardConditionKind.Compare).Select(c => (object)c.Constant);
        return GetVariations(type, [.. constants, .. guardConstants], conditions);
    }

    /// <summary>
    /// 基準の値を選ぶ（数値は 0 だと掛け算などの結果がすべて 0 になり確認にならないため、1 を優先する）
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <returns>基準の値（ガードを通過する値がなければnull）</returns>
    private static TestValue? FindBaseValue(ITypeSymbol type, List<GuardCondition> conditions)
    {
        var one = TestValue.FromNumber(1);
        if (ConditionParser.IsNumeric(type) && ValueSolver.IsRepresentable(one, type) && !conditions.Any(c => c.IsViolatedBy(one))) return one;

        return ValueSolver.FindValid(type, conditions, allowMocks: false);
    }

    /// <summary>
    /// 基準の入力から、引数を1つずつ変えた入力を作る（重複は除き、件数に上限を設ける）
    /// </summary>
    /// <param name="baseRow">基準の入力</param>
    /// <param name="variations">引数ごとの境界値</param>
    /// <returns>入力の一覧</returns>
    private static List<object?[]> CombineOneAtATime(object?[] baseRow, List<object?>[] variations)
    {
        var rows = new List<object?[]> { baseRow };
        for (var i = 0; i < variations.Length; i++)
        {
            foreach (var value in variations[i])
            {
                var row = (object?[])baseRow.Clone();
                row[i] = value;
                if (!rows.Any(r => r.SequenceEqual(row))) rows.Add(row);
            }
        }

        return rows.Take(MaxRows).ToList();
    }

    /// <summary>
    /// 引数の型ごとに、境界値の候補を作る（ガードで例外になる値は除く）
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="constants">本体でその引数と比べている定数</param>
    /// <param name="conditions">その引数のガード条件</param>
    /// <returns>境界値の候補</returns>
    private static List<object?> GetVariations(ITypeSymbol type, List<object> constants, List<GuardCondition> conditions)
    {
        IEnumerable<TestValue> candidates = type.SpecialType switch
        {
            SpecialType.System_Boolean => [TestValue.False],
            SpecialType.System_String => constants.OfType<string>().Append(string.Empty).Append(" ").Select(TestValue.FromString),
            _ => GetNumberCandidates(constants),
        };

        var values = candidates
            .Where(v => ValueSolver.IsRepresentable(v, type) && !conditions.Any(c => c.IsViolatedBy(v)))
            .Select(v => ToClrValue(v, type))
            .ToList();
        if (type.SpecialType == SpecialType.System_Boolean) values.Add(true);
        return values;
    }

    /// <summary>
    /// 数値の境界値（0 と、比べている定数とその前後）を作る。比較がなければ代表値（0, -1）を使う
    /// </summary>
    /// <param name="constants">本体でその引数と比べている定数</param>
    /// <returns>数値の候補</returns>
    private static IEnumerable<TestValue> GetNumberCandidates(List<object> constants)
    {
        var numbers = constants.Where(c => c is not string and not bool and not char)
            .Select(c => Convert.ToDecimal(c, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (numbers.Count == 0) return [TestValue.FromNumber(0), TestValue.FromNumber(-1)];

        return numbers.SelectMany(c => new[] { c - 1, c, c + 1 }).Prepend(0).Distinct().OrderBy(n => n).Select(TestValue.FromNumber);
    }

    /// <summary>
    /// 本体で引数と比べている定数（比較演算・定数パターン・関係パターン）を集める
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド</param>
    /// <returns>引数ごとの定数</returns>
    private static Dictionary<IParameterSymbol, List<object>> CollectBoundaryConstants(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member)
    {
        var result = new Dictionary<IParameterSymbol, List<object>>(SymbolEqualityComparer.Default);
        foreach (var node in declaration.DescendantNodes())
        {
            var (subject, constantExpression) = node switch
            {
                BinaryExpressionSyntax binary when IsComparison(binary) => SplitComparison(binary, model),
                ConstantPatternSyntax constantPattern => (GetPatternSubject(constantPattern), constantPattern.Expression),
                CaseSwitchLabelSyntax caseLabel => ((caseLabel.Parent?.Parent as SwitchStatementSyntax)?.Expression, caseLabel.Value),
                RelationalPatternSyntax relational => (GetPatternSubject(relational), relational.Expression),
                _ => (null, null),
            };
            if (subject is null || constantExpression is null) continue;

            var parameter = ConditionParser.GetParameter(subject, model, member);
            var constant = model.GetConstantValue(constantExpression);
            if (parameter is null || !constant.HasValue || constant.Value is null) continue;

            if (!result.TryGetValue(parameter, out var list)) result[parameter] = list = [];
            if (!list.Contains(constant.Value)) list.Add(constant.Value);
        }

        return result;
    }

    /// <summary>
    /// 比較演算（==, !=, &lt;, &lt;=, &gt;, &gt;=）かを判定する
    /// </summary>
    /// <param name="binary">二項演算の式</param>
    /// <returns>比較演算ならtrue</returns>
    private static bool IsComparison(BinaryExpressionSyntax binary)
    {
        return binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;
    }

    /// <summary>
    /// 比較演算を「対象の式」と「定数の式」に分ける（定数が左辺でもよい）
    /// </summary>
    /// <param name="binary">比較演算の式</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>対象の式と定数の式</returns>
    private static (ExpressionSyntax? Subject, ExpressionSyntax? Constant) SplitComparison(BinaryExpressionSyntax binary, SemanticModel model)
    {
        if (model.GetConstantValue(binary.Right).HasValue) return (binary.Left, binary.Right);
        if (model.GetConstantValue(binary.Left).HasValue) return (binary.Right, binary.Left);
        return (null, null);
    }

    /// <summary>
    /// パターンが照合する対象の式（switch 式・is 式の左辺）を求める
    /// </summary>
    /// <param name="pattern">パターン</param>
    /// <returns>対象の式（求められなければnull）</returns>
    private static ExpressionSyntax? GetPatternSubject(PatternSyntax pattern)
    {
        for (SyntaxNode? node = pattern.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case SwitchExpressionSyntax switchExpression:
                    return switchExpression.GoverningExpression;
                case IsPatternExpressionSyntax isPattern:
                    return isPattern.Expression;
                case SwitchStatementSyntax switchStatement:
                    return switchStatement.Expression;
                case PatternSyntax or SwitchExpressionArmSyntax or CasePatternSwitchLabelSyntax or SwitchSectionSyntax:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// テストの値を、引数の型の .NET の値にする
    /// </summary>
    /// <param name="value">テストの値</param>
    /// <param name="type">引数の型</param>
    /// <returns>.NET の値</returns>
    private static object? ToClrValue(TestValue value, ITypeSymbol type)
    {
        return value.Kind switch
        {
            TestValueKind.String => value.Text,
            TestValueKind.False => false,
            TestValueKind.Number => type.SpecialType switch
            {
                SpecialType.System_Int32 => (int)value.Number,
                SpecialType.System_Int64 => (long)value.Number,
                SpecialType.System_Double => (double)value.Number,
                _ => value.Number,
            },
            _ => null,
        };
    }
}

/// <summary>
/// 基準の値（int / long / double / decimal）を decimal として取り出す
/// </summary>
internal static class ClrOperationsBridge
{
    /// <summary>
    /// 数値なら decimal に変換する
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="number">変換した値</param>
    /// <returns>数値ならtrue</returns>
    public static bool TryToDecimal(object? value, out decimal number)
    {
        number = value switch
        {
            int i => i,
            long l => l,
            double d when -1e15 < d && d < 1e15 => (decimal)d,
            decimal m => m,
            _ => decimal.MinValue,
        };
        return number != decimal.MinValue;
    }
}
