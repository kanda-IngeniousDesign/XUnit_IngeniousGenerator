using XUnit_IngeniousGenerator.Core.Evaluation;
using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 戻り値テストを計画する（静的に計算できれば期待値をリテラルで書き、できなければ実行結果を Verify で記録する）
/// </summary>
internal sealed class ReturnValueTestPlanner
{
    private const string TasksNamespace = "System.Threading.Tasks";

    private readonly GenerationContext _context;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="context">生成の情報</param>
    public ReturnValueTestPlanner(GenerationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 型が境界値の入力・計算した期待値として扱えるか（int, long, double, decimal, bool, string）を判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>扱えるならtrue</returns>
    public static bool IsSupportedType(ITypeSymbol type)
    {
        return DataTypes.IsPrimitive(type);
    }

    /// <summary>
    /// メソッド1つ分の戻り値テストを計画する
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="guards">先頭のガード</param>
    /// <param name="display">レポート用の表示名</param>
    /// <returns>戻り値テスト（作らない場合はnull）</returns>
    public PlannedTest? Plan(IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, MemberGuards guards, string display)
    {
        if (member.MethodKind != MethodKind.Ordinary) return null;

        // ファイル・DB・通信などは環境によって失敗するため、テストを作らずにUIオーケストレーター側へ回す
        var dependency = ExternalDependencyDetector.Find(declaration, model);
        if (dependency is { NonDeterministic: false })
        {
            _context.NotGeneratableItems.Add($"{display}: {dependency.Value.Reason}、単体テストでは担保しません（UIオーケストレーター側で確認）");
            return null;
        }

        // 戻り値がない、または時刻・乱数で戻り値が変わるメソッドは、境界値の入力で例外が出ないことだけを確かめる
        var checksOnlyNoThrow = member.ReturnsVoid || IsTaskWithoutResult(member.ReturnType) || dependency is not null;
        var inputs = BoundaryInputGenerator.Generate(
            member, declaration, model, guards, _context.Options.UseMoq, varyCollections: checksOnlyNoThrow, out var failedParameter,
            construct: _context.Constructor.CreateDependency);
        if (inputs is null)
        {
            var hint = _context.Options.UseMoq ? string.Empty : "。テストプロジェクトに Moq を追加すると、インターフェースの依存を自動でモックします";
            return Manual(display, $"引数 {failedParameter!.Name} の値を作れません{hint}", checksOnlyNoThrow);
        }

        if (checksOnlyNoThrow)
        {
            if (dependency is not null)
            {
                _context.NotGeneratableItems.Add($"{display}: {dependency.Value.Reason}、戻り値は確認せず、例外が出ないことだけを確認します");
            }

            var suffix = inputs.DataParameters.Count == 0 ? "_DefaultInputs_DoesNotThrow" : "_BoundaryValues_DoesNotThrow";
            return new NoThrowTestCase(member, MemberDisplay.GetTestName(member) + suffix, inputs);
        }

        var reason = GetStaticReason(member, inputs);
        if (reason is null)
        {
            var computed = TryCompute(member, declaration, model, inputs, out reason);
            if (computed is not null || reason is null) return computed;
        }

        if (!_context.Options.UseVerify)
        {
            return Manual(display, $"戻り値を静的に計算できません（{reason}）。テストプロジェクトに Verify.Xunit を追加すると、実行結果を記録するテストを生成します");
        }

        var testName = inputs.DataParameters.Count == 0
            ? $"{MemberDisplay.GetTestName(member)}_DefaultInputs_MatchesSnapshot"
            : $"{MemberDisplay.GetTestName(member)}_BoundaryValues_MatchesSnapshot";
        return new SnapshotTestCase(member, testName, inputs);
    }

    /// <summary>
    /// 静的な計算の対象外となる理由を返す（引数・戻り値の型で判断できるもの）
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="inputs">テストの入力</param>
    /// <returns>対象外の理由（計算を試せる場合はnull）</returns>
    private static string? GetStaticReason(IMethodSymbol member, BoundaryInputs inputs)
    {
        var structured = inputs.DataParameters.FirstOrDefault(p => !IsSupportedType(p.Type));
        if (structured is not null)
        {
            return $"引数 {structured.Name} の型 {structured.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} は計算の対象外";
        }

        if (0 < inputs.FixedValues.Count)
        {
            var parameter = inputs.FixedValues.Keys.First();
            return $"引数 {parameter.Name} の型 {parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} は計算の対象外";
        }

        if (!IsSupportedType(member.ReturnType)) return $"戻り値の型 {member.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
        return null;
    }

    /// <summary>
    /// 境界値の入力ごとに戻り値を静的に計算する
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="inputs">テストの入力（すべての引数が境界値の対象）</param>
    /// <param name="reason">計算できない場合の理由（計算できた、またはすべて例外になった場合はnull）</param>
    /// <returns>戻り値テスト（計算できない、またはすべて例外になった場合はnull）</returns>
    private static ReturnValueTestCase? TryCompute(
        IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, BoundaryInputs inputs, out string? reason)
    {
        var rows = new List<(IReadOnlyList<object?>, object?)>(inputs.Rows.Count);
        foreach (var input in inputs.Rows)
        {
            var result = MethodInterpreter.Evaluate(declaration, model, member, input);
            reason = result switch
            {
                { Kind: EvaluationKind.Unsupported } => result.Reason,
                { Kind: EvaluationKind.Returned, Value: null } => "null を返す入力があります",
                { Kind: EvaluationKind.Returned, Value: double d } when double.IsNaN(d) || double.IsInfinity(d) => "NaN または無限大を返す入力があります",
                _ => null,
            };
            if (reason is not null) return null;

            // 例外になる入力は、例外テストの対象のため戻り値テストには含めない
            if (result.Kind == EvaluationKind.Returned) rows.Add((input, result.Value));
        }

        reason = null;
        return rows.Count == 0 ? null : new ReturnValueTestCase(member, $"{MemberDisplay.GetTestName(member)}_BoundaryValues_ReturnsExpected", rows);
    }

    /// <summary>
    /// 値を持たない Task / ValueTask かを判定する（記録する戻り値がない）
    /// </summary>
    /// <param name="type">戻り値の型</param>
    /// <returns>値を持たない Task / ValueTask ならtrue</returns>
    private static bool IsTaskWithoutResult(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { Name: "Task" or "ValueTask", IsGenericType: false } named
            && named.ContainingNamespace?.ToDisplayString() == TasksNamespace;
    }

    /// <summary>
    /// 要手動として記録し、テストは作らない
    /// </summary>
    /// <param name="display">レポート用の表示名</param>
    /// <param name="reason">理由</param>
    /// <param name="checksOnlyNoThrow">例外が出ないことの確認か（false なら戻り値テスト）</param>
    /// <returns>常にnull</returns>
    private PlannedTest? Manual(string display, string reason, bool checksOnlyNoThrow = false)
    {
        var kind = checksOnlyNoThrow ? "例外が出ないことの確認" : "戻り値テスト";
        _context.ManualItems.Add($"{display}: {kind}: {reason}");
        return null;
    }
}
