using System.Globalization;
using XUnit_IngeniousGenerator.Core.Evaluation;
using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// ガードより後ろにある throw と、解析できないガード（引数どうしの比較・引数のプロパティの比較など）とそれ以降の throw について、
/// 静的な評価でそこに到達する境界値の入力を探し、例外テストを計画する（到達する入力が見つからない throw は要手動として記録する）
/// </summary>
internal sealed class LaterThrowTestPlanner
{
    private readonly GenerationContext _context;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="context">生成の情報</param>
    public LaterThrowTestPlanner(GenerationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// ガードより後ろの throw の例外テストを計画する
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="guards">先頭のガード</param>
    /// <param name="display">レポート用の表示名</param>
    /// <returns>例外テストの一覧</returns>
    public List<PlannedTest> Plan(IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, MemberGuards guards, string display)
    {
        // 最初の解析できないガードより前のガードだけを、入力が避ける条件にする（それ以降の throw は、ここで到達する入力を探す）
        var recognizedCount = guards.Statements.TakeWhile(s => s.IsRecognized).Count();
        var leadingGuards = new MemberGuards(guards.Statements.Take(recognizedCount).ToList(), 0);
        var sites = CollectLaterThrowSites(declaration, model, recognizedCount);
        if (sites.Count == 0) return [];

        var tests = new List<PlannedTest>();
        var covered = new HashSet<SyntaxNode>();
        if (CanEvaluate(member, declaration))
        {
            var inputs = BoundaryInputGenerator.Generate(
                member, declaration, model, leadingGuards, allowMocks: false, varyCollections: false, out _, avoidBodyThrows: false);
            if (inputs is not null) tests.AddRange(FindThrowingInputs(member, declaration, model, inputs, sites, covered));
        }

        ReportUncovered(guards, recognizedCount, sites, covered, display);
        return tests;
    }

    /// <summary>
    /// 静的な評価で入力を探せる関数か（引数がすべて基本の型で、コンストラクタは base(...) などの初期化子がないもの）を判定する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <param name="declaration">宣言</param>
    /// <returns>探せるならtrue</returns>
    private static bool CanEvaluate(IMethodSymbol member, BaseMethodDeclarationSyntax declaration)
    {
        if (!member.Parameters.All(p => DataTypes.IsPrimitive(p.Type))) return false;

        return member.MethodKind == MethodKind.Ordinary
            || (member.MethodKind == MethodKind.Constructor && declaration is ConstructorDeclarationSyntax { Initializer: null });
    }

    /// <summary>
    /// 到達する入力が見つからなかった throw を要手動として記録する（解析できないガードは条件の文字列を示す）
    /// </summary>
    /// <param name="guards">先頭のガード</param>
    /// <param name="recognizedCount">最初の解析できないガードの位置</param>
    /// <param name="sites">対象の throw</param>
    /// <param name="covered">テストを作った throw</param>
    /// <param name="display">レポート用の表示名</param>
    private void ReportUncovered(MemberGuards guards, int recognizedCount, HashSet<SyntaxNode> sites, HashSet<SyntaxNode> covered, string display)
    {
        var uncovered = new HashSet<SyntaxNode>(sites.Where(s => !covered.Contains(s)));
        foreach (var statement in guards.Statements.Skip(recognizedCount).Where(s => !s.IsRecognized))
        {
            var site = uncovered.FirstOrDefault(s => statement.Syntax is not null && statement.Syntax.Contains(s));
            if (site is null) continue;

            uncovered.Remove(site);
            _context.ManualItems.Add($"{display}: {Messages.Get("UnreachableGuard", statement.UnrecognizedText)}");
        }

        if (0 < uncovered.Count)
        {
            _context.ManualItems.Add($"{display}: {Messages.Get("UnreachableLaterThrows", uncovered.Count)}");
        }
    }

    /// <summary>
    /// ガード文より後ろにある throw 文・throw 式と、そこから呼んでいる同じクラスの関数の中の throw を集める
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="recognizedCount">先頭から続く、解析できたガードの数（これより後ろの throw を集める）</param>
    /// <returns>throw の構文</returns>
    private static HashSet<SyntaxNode> CollectLaterThrowSites(BaseMethodDeclarationSyntax declaration, SemanticModel model, int recognizedCount)
    {
        var statements = declaration.Body?.Statements.Skip(recognizedCount) ?? [];
        var callees = SameClassCall.FindReachable(declaration, model).SelectMany(c => c.Declaration.DescendantNodes());
        return new HashSet<SyntaxNode>(statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .Concat(callees)
            .Where(n => n is ThrowStatementSyntax or ThrowExpressionSyntax || IsThrowHelper(n, model)));
    }

    /// <summary>
    /// ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max) などの、例外を投げる標準のメソッドの呼び出しかを判定する
    /// </summary>
    /// <param name="node">構文</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>呼び出しならtrue</returns>
    private static bool IsThrowHelper(SyntaxNode node, SemanticModel model)
    {
        return node is InvocationExpressionSyntax { Parent: ExpressionStatementSyntax } invocation
            && invocation.SyntaxTree == model.SyntaxTree
            && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { IsStatic: true } method
            && method.Name.StartsWith("ThrowIf", StringComparison.Ordinal)
            && method.ContainingType.ToDisplayString() is "System.ArgumentException" or "System.ArgumentNullException" or "System.ArgumentOutOfRangeException";
    }

    /// <summary>
    /// 境界値の入力を順に評価し、まだテストのない throw に到達した入力を例外テストにする。
    /// 「||」でつながった if の条件は、条件ごとに例外になる入力を探す（value &lt; min || max &lt; value なら、小さい側と大きい側の両方）
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="inputs">境界値の入力（すべての引数が基本の型）</param>
    /// <param name="sites">ガードより後ろの throw</param>
    /// <param name="covered">テストを作った throw の記録先</param>
    /// <returns>例外テストの一覧</returns>
    private static IEnumerable<PlannedTest> FindThrowingInputs(
        IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, BoundaryInputs inputs, HashSet<SyntaxNode> sites, HashSet<SyntaxNode> covered)
    {
        var baseRow = inputs.Rows[0];
        var coveredConditions = new HashSet<(SyntaxNode Site, int Condition)>();
        var testNames = new HashSet<string>();
        foreach (var row in inputs.Rows)
        {
            // 評価できない入力があっても、ほかの入力では throw に到達する場合がある（コンストラクタのフィールドへの代入など）
            var result = MethodInterpreter.Evaluate(declaration, model, member, row);
            if (result.Kind == EvaluationKind.Unsupported) continue;
            if (result.Kind != EvaluationKind.Threw || result.ExceptionType is null || result.ThrowSite is null) continue;
            if (!sites.Contains(result.ThrowSite)) continue;

            // この入力で成り立つ条件のうち、まだテストのない条件があればテストにする
            var conditions = GetHoldingConditions(result.ThrowSite, declaration, model, member, row);
            if (!conditions.Any(c => !coveredConditions.Contains((result.ThrowSite, c)))) continue;

            var condition = DescribeChangedInput(member, baseRow, row);
            var testName = $"{MemberDisplay.GetTestName(member)}_{condition}_Throws{result.ExceptionType.Name}";
            if (!testNames.Add(testName)) continue;

            covered.Add(result.ThrowSite);
            foreach (var index in conditions) coveredConditions.Add((result.ThrowSite, index));
            yield return new ComputedExceptionTestCase(member, testName, result.ExceptionType, result.ParamName, row);
        }
    }

    /// <summary>
    /// throw を囲む if の条件が「||」でつながっていれば、入力で成り立つ条件の番号を返す（つながっていなければ、条件全体を 0 番とする）
    /// </summary>
    /// <param name="site">throw の構文</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="member">メソッド</param>
    /// <param name="row">入力</param>
    /// <returns>成り立つ条件の番号</returns>
    private static List<int> GetHoldingConditions(
        SyntaxNode site, BaseMethodDeclarationSyntax declaration, SemanticModel model, IMethodSymbol member, object?[] row)
    {
        var ifStatement = site.Parent is BlockSyntax { Statements.Count: 1 } block ? block.Parent as IfStatementSyntax : site.Parent as IfStatementSyntax;
        if (ifStatement is null || !declaration.Contains(ifStatement)) return [0];

        var parts = SplitOr(ifStatement.Condition);
        if (parts.Count < 2) return [0];

        // 条件を評価できない場合は、条件全体を 0 番として扱う（入力1つでテスト1つ）
        var holding = parts.Select((part, index) => (index, value: MethodInterpreter.EvaluateCondition(declaration, model, member, row, part)))
            .ToList();
        return holding.Any(h => h.value is null) ? [0] : holding.Where(h => h.value == true).Select(h => h.index + 1).ToList();
    }

    /// <summary>
    /// 条件を「||」で分ける（括弧の中もたどる）
    /// </summary>
    /// <param name="condition">条件</param>
    /// <returns>分けた条件</returns>
    private static List<ExpressionSyntax> SplitOr(ExpressionSyntax condition)
    {
        return condition switch
        {
            ParenthesizedExpressionSyntax parenthesized => SplitOr(parenthesized.Expression),
            BinaryExpressionSyntax binary when binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalOrExpression) =>
                [.. SplitOr(binary.Left), .. SplitOr(binary.Right)],
            _ => [condition],
        };
    }

    /// <summary>
    /// 基準の入力から変えた引数と値を、テスト名の条件部分にする（例: DigitsIsMinus1）
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="baseRow">基準の入力</param>
    /// <param name="row">例外になった入力</param>
    /// <returns>テスト名の条件部分</returns>
    internal static string DescribeChangedInput(IMethodSymbol member, object?[] baseRow, object?[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (Equals(baseRow[i], row[i])) continue;

            var name = member.Parameters[i].Name.TrimStart('_', '@');
            var pascal = name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
            return pascal + DescribeValue(row[i]);
        }

        return "DefaultInputs";
    }

    /// <summary>
    /// 値をテスト名の条件部分にする（IsNull, IsEmpty, IsWhiteSpace, IsMinus1, IsTrue など）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>テスト名の条件部分</returns>
    private static string DescribeValue(object? value)
    {
        return value switch
        {
            _ when TypeExtremes.Describe(value) is { } extreme => extreme,
            null => "IsNull",
            string { Length: 0 } => "IsEmpty",
            string text when string.IsNullOrWhiteSpace(text) => "IsWhiteSpace",
            string text => "Is" + new string(text.Where(char.IsLetterOrDigit).Take(20).ToArray()),
            bool flag => flag ? "IsTrue" : "IsFalse",
            DateTime date => "Is" + date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyyMMdd" : "yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            DateOnlyValue date => "Is" + date.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            TimeSpan span => "Is" + TestValue.FromNumber((decimal)span.TotalSeconds).ConditionName.Substring(2) + "Seconds",
            _ when ClrOperations.IsNumber(value) => TestValue.FromNumber(Convert.ToDecimal(value, CultureInfo.InvariantCulture)).ConditionName,
            _ => "IsSpecified",
        };
    }
}
