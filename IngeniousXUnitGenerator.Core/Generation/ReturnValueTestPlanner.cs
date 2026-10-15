using IngeniousXUnitGenerator.Core.Evaluation;
using IngeniousXUnitGenerator.Core.Guards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// 境界値の入力ごとに戻り値を静的に計算し、戻り値テストを計画する
/// </summary>
internal sealed class ReturnValueTestPlanner
{
    private readonly List<string> _manualItems;

    /// <summary>
    /// 計画を立てる準備をする
    /// </summary>
    /// <param name="manualItems">要手動項目の追加先</param>
    public ReturnValueTestPlanner(List<string> manualItems)
    {
        _manualItems = manualItems;
    }

    /// <summary>
    /// 型が戻り値テストの引数・戻り値として扱えるか（int, long, double, decimal, bool, string）を判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>扱えるならtrue</returns>
    public static bool IsSupportedType(ITypeSymbol type)
    {
        return type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double
            or SpecialType.System_Decimal or SpecialType.System_Boolean or SpecialType.System_String;
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
    public ReturnValueTestCase? Plan(
        IMethodSymbol member, BaseMethodDeclarationSyntax declaration, SemanticModel model, MemberGuards guards, string display)
    {
        if (member.MethodKind != MethodKind.Ordinary || member.ReturnsVoid) return null;

        var unsupported = new[] { member.ReturnType }.Concat(member.Parameters.Select(p => p.Type)).FirstOrDefault(t => !IsSupportedType(t));
        if (unsupported is not null) return Manual(display, $"型 {unsupported.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} は未対応です");

        var inputs = BoundaryInputGenerator.Generate(member, declaration, model, guards);
        if (inputs is null) return Manual(display, "ガードを通過する引数の値を決められません");

        var rows = new List<(IReadOnlyList<object?>, object?)>(inputs.Count);
        foreach (var input in inputs)
        {
            var result = MethodInterpreter.Evaluate(declaration, model, member, input);
            switch (result.Kind)
            {
                case EvaluationKind.Unsupported:
                    return Manual(display, $"戻り値を静的に計算できません（{result.Reason}）");
                case EvaluationKind.Returned when result.Value is null:
                    return Manual(display, "null を返す入力があります");
                case EvaluationKind.Returned when result.Value is double d && (double.IsNaN(d) || double.IsInfinity(d)):
                    return Manual(display, "NaN または無限大を返す入力があります");
                case EvaluationKind.Returned:
                    rows.Add((input, result.Value));
                    break;
            }

            // 例外になる入力は、例外テストの対象のため戻り値テストには含めない
        }

        if (rows.Count == 0) return null;
        return new ReturnValueTestCase(member, $"{MemberDisplay.GetTestName(member)}_BoundaryValues_ReturnsExpected", rows);
    }

    /// <summary>
    /// 要手動として記録し、テストは作らない
    /// </summary>
    /// <param name="display">レポート用の表示名</param>
    /// <param name="reason">理由</param>
    /// <returns>常にnull</returns>
    private ReturnValueTestCase? Manual(string display, string reason)
    {
        _manualItems.Add($"{display}: 戻り値テスト: {reason}");
        return null;
    }
}
