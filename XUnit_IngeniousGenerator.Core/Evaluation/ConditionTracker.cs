using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// 評価中に、判定（if・三項演算子・ループの条件）と、その中の &amp;&amp; ・ || でつながった各要素が成立したかを記録する
/// （条件の網羅を目指して入力を選ぶために使う）
/// </summary>
public sealed class ConditionTracker
{
    private readonly Stack<ConditionObservation> _active = new();

    /// <summary>判定1回ごとの記録</summary>
    public List<ConditionObservation> Observations { get; } = [];

    /// <summary>
    /// 判定の評価を始める
    /// </summary>
    /// <param name="decision">判定の条件式</param>
    public void Begin(ExpressionSyntax decision)
    {
        _active.Push(new ConditionObservation(decision, AtomsOf(decision)));
    }

    /// <summary>
    /// 式の値を記録する（評価中の判定の要素なら）
    /// </summary>
    /// <param name="expression">式</param>
    /// <param name="value">値</param>
    public void Record(ExpressionSyntax expression, object? value)
    {
        if (_active.Count == 0 || value is not bool flag) return;

        var observation = _active.Peek();
        var index = observation.IndexOf(expression);
        if (0 <= index) observation.Values[index] = flag;
    }

    /// <summary>
    /// 判定の評価を終え、結果を記録する
    /// </summary>
    /// <param name="outcome">判定の結果</param>
    public void End(bool outcome)
    {
        var observation = _active.Pop();
        observation.Outcome = outcome;
        Observations.Add(observation);
    }

    /// <summary>
    /// 判定の評価が途中で終わった（例外など）ので、記録しない
    /// </summary>
    public void Abort()
    {
        if (0 < _active.Count) _active.Pop();
    }

    /// <summary>
    /// 判定の要素（&amp;&amp; ・ || ・ ! ・ 括弧の内側の、それ以上分けられない条件）を返す
    /// </summary>
    /// <param name="condition">判定の条件式</param>
    /// <returns>要素</returns>
    private static List<ExpressionSyntax> AtomsOf(ExpressionSyntax condition)
    {
        return condition switch
        {
            ParenthesizedExpressionSyntax parenthesized => AtomsOf(parenthesized.Expression),
            PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression) => AtomsOf(not.Operand),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) =>
                [.. AtomsOf(binary.Left), .. AtomsOf(binary.Right)],
            _ => [condition],
        };
    }
}

/// <summary>
/// 判定1回分の記録（各要素の値と、判定の結果）
/// </summary>
public sealed class ConditionObservation
{
    /// <summary>
    /// 記録を始める
    /// </summary>
    /// <param name="decision">判定の条件式</param>
    /// <param name="atoms">判定の要素</param>
    public ConditionObservation(ExpressionSyntax decision, IReadOnlyList<ExpressionSyntax> atoms)
    {
        Decision = decision;
        Atoms = atoms;
        Values = new bool?[atoms.Count];
    }

    /// <summary>判定の条件式</summary>
    public ExpressionSyntax Decision { get; }

    /// <summary>判定の要素</summary>
    public IReadOnlyList<ExpressionSyntax> Atoms { get; }

    /// <summary>要素ごとの値（短絡評価で評価されなかった要素は null）</summary>
    public bool?[] Values { get; }

    /// <summary>判定の結果</summary>
    public bool Outcome { get; set; }

    /// <summary>
    /// 要素の位置を返す
    /// </summary>
    /// <param name="expression">式</param>
    /// <returns>位置（要素でなければ -1）</returns>
    public int IndexOf(ExpressionSyntax expression)
    {
        for (var i = 0; i < Atoms.Count; i++)
        {
            if (Atoms[i] == expression) return i;
        }

        return -1;
    }
}
