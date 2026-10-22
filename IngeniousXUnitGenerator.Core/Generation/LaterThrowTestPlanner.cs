using System.Globalization;
using IngeniousXUnitGenerator.Core.Evaluation;
using IngeniousXUnitGenerator.Core.Guards;
using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// ガードより後ろにある throw について、静的な評価でそこに到達する境界値の入力を探し、例外テストを計画する
/// （到達する入力が見つからない throw は要手動として記録する）
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
        var sites = CollectLaterThrowSites(declaration, guards);
        if (sites.Count == 0) return [];

        var tests = new List<PlannedTest>();
        var covered = new HashSet<SyntaxNode>();
        if (member.MethodKind == MethodKind.Ordinary && member.Parameters.All(p => DataTypes.IsPrimitive(p.Type)))
        {
            var inputs = BoundaryInputGenerator.Generate(
                member, declaration, model, guards, allowMocks: false, varyCollections: false, out _, avoidBodyThrows: false);
            if (inputs is not null) tests.AddRange(FindThrowingInputs(member, declaration, model, inputs, sites, covered));
        }

        var uncovered = sites.Count - covered.Count;
        if (0 < uncovered)
        {
            _context.ManualItems.Add($"{display}: ガードより後ろにある throw {uncovered} 件は、そこに到達する入力を求められないため例外テストの対象外です");
        }

        return tests;
    }

    /// <summary>
    /// ガード文より後ろにある throw 文・throw 式を集める
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="guards">先頭のガード</param>
    /// <returns>throw の構文</returns>
    private static HashSet<SyntaxNode> CollectLaterThrowSites(BaseMethodDeclarationSyntax declaration, MemberGuards guards)
    {
        var statements = declaration.Body?.Statements.Skip(guards.Statements.Count) ?? [];
        return new HashSet<SyntaxNode>(statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .Where(n => n is ThrowStatementSyntax or ThrowExpressionSyntax));
    }

    /// <summary>
    /// 境界値の入力を順に評価し、まだテストのない throw に到達した入力を例外テストにする
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
        foreach (var row in inputs.Rows)
        {
            var result = MethodInterpreter.Evaluate(declaration, model, member, row);
            if (result.Kind == EvaluationKind.Unsupported) yield break;
            if (result.Kind != EvaluationKind.Threw || result.ExceptionType is null || result.ThrowSite is null) continue;
            if (!sites.Contains(result.ThrowSite) || !covered.Add(result.ThrowSite)) continue;

            var condition = DescribeChangedInput(member, baseRow, row);
            var testName = $"{MemberDisplay.GetTestName(member)}_{condition}_Throws{result.ExceptionType.Name}";
            yield return new ComputedExceptionTestCase(member, testName, result.ExceptionType, result.ParamName, row);
        }
    }

    /// <summary>
    /// 基準の入力から変えた引数と値を、テスト名の条件部分にする（例: DigitsIsMinus1）
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="baseRow">基準の入力</param>
    /// <param name="row">例外になった入力</param>
    /// <returns>テスト名の条件部分</returns>
    private static string DescribeChangedInput(IMethodSymbol member, object?[] baseRow, object?[] row)
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
            null => "IsNull",
            string { Length: 0 } => "IsEmpty",
            string text when string.IsNullOrWhiteSpace(text) => "IsWhiteSpace",
            string text => "Is" + new string(text.Where(char.IsLetterOrDigit).Take(20).ToArray()),
            bool flag => flag ? "IsTrue" : "IsFalse",
            _ when ClrOperations.IsNumber(value) => TestValue.FromNumber(Convert.ToDecimal(value, CultureInfo.InvariantCulture)).ConditionName,
            _ => "IsSpecified",
        };
    }
}
