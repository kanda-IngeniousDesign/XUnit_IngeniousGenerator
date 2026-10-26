using XUnit_IngeniousGenerator.Core.Evaluation;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 静的に計算できた戻り値テストに、引数の型の端の値（int.MinValue / int.MaxValue、null など）の入力を加える。
/// 戻り値が出ればその値を期待値の行に、実行時の例外（NullReferenceException・OverflowException など）が出れば例外テストにする
/// （期待値は今のコードの挙動で、引数のチェック漏れなどに PR のレビューで気づけるようにするため）
/// </summary>
internal static class TypeExtremeTestPlanner
{
    // 戻り値テストの行数の上限（境界値の入力と合わせて）
    private const int MaxRows = 40;

    /// <summary>
    /// 戻り値テストに型の端の値の行を加え、例外になる入力は例外テストにする
    /// </summary>
    /// <param name="test">静的に計算できた戻り値テスト</param>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>行を加えた戻り値テストと、例外テスト</returns>
    public static IEnumerable<PlannedTest> Extend(ReturnValueTestCase test, BaseMethodDeclarationSyntax declaration, SemanticModel model)
    {
        var member = test.Member;
        var rows = test.Rows.ToList();
        var baseRow = rows[0].Inputs.ToArray();
        var exceptions = new List<PlannedTest>();
        var checkOverflow = model.Compilation.Options.CheckOverflow;

        foreach (var row in BuildRows(member, baseRow, checkOverflow).Where(row => !rows.Any(r => r.Inputs.SequenceEqual(row))))
        {
            var result = MethodInterpreter.Evaluate(declaration, model, member, row);
            if (IsUsableReturn(result))
            {
                if (rows.Count < MaxRows) rows.Add((row, result.Value));
            }
            else if (IsRuntimeException(result, model.Compilation))
            {
                var testName = $"{MemberDisplay.GetTestName(member)}_{LaterThrowTestPlanner.DescribeChangedInput(member, baseRow, row)}_Throws{result.ExceptionType!.Name}";
                exceptions.Add(new ComputedExceptionTestCase(member, testName, result.ExceptionType, null, row));
            }
        }

        yield return new ReturnValueTestCase(member, test.TestName, rows);
        foreach (var exception in exceptions) yield return exception;
    }

    /// <summary>
    /// 基準の入力から、引数を1つずつ型の端の値に変えた入力を作る
    /// </summary>
    /// <param name="member">メソッド</param>
    /// <param name="baseRow">基準の入力</param>
    /// <param name="checkOverflow">オーバーフローを検査するコンパイル設定か</param>
    /// <returns>入力</returns>
    private static IEnumerable<object?[]> BuildRows(IMethodSymbol member, object?[] baseRow, bool checkOverflow)
    {
        for (var i = 0; i < member.Parameters.Length; i++)
        {
            foreach (var extreme in TypeExtremes.For(member.Parameters[i], checkOverflow))
            {
                var row = (object?[])baseRow.Clone();
                row[i] = extreme;
                yield return row;
            }
        }
    }

    /// <summary>
    /// 期待値として書ける戻り値か（null・NaN・無限大は除く）を判定する
    /// </summary>
    /// <param name="result">評価の結果</param>
    /// <returns>書けるならtrue</returns>
    private static bool IsUsableReturn(EvaluationResult result)
    {
        return result is { Kind: EvaluationKind.Returned, Value: not null }
            && !(result.Value is double d && (double.IsNaN(d) || double.IsInfinity(d)));
    }

    /// <summary>
    /// 引数のチェック（throw 文・ThrowIf～・ArgumentException の系統）ではなく、実行時に起きる例外かを判定する。
    /// 引数のチェックによる例外は、ガードの例外テストで確かめているため、ここでは作らない
    /// </summary>
    /// <param name="result">評価の結果</param>
    /// <param name="compilation">コンパイル</param>
    /// <returns>実行時の例外ならtrue</returns>
    private static bool IsRuntimeException(EvaluationResult result, Compilation compilation)
    {
        if (result.Kind != EvaluationKind.Threw || result.ExceptionType is null) return false;
        if (result.ThrowSite is ThrowStatementSyntax or ThrowExpressionSyntax) return false;

        var argumentException = compilation.GetTypeByMetadataName("System.ArgumentException");
        for (var type = result.ExceptionType; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, argumentException)) return false;
        }

        return true;
    }
}
