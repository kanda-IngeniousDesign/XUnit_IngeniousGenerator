using IngeniousXUnitGenerator.Core.Values;

namespace IngeniousXUnitGenerator.Core.Guards;

/// <summary>
/// ガードが例外を投げる条件の種類
/// </summary>
public enum GuardConditionKind
{
    /// <summary>引数がnull</summary>
    Null,

    /// <summary>引数が空文字列</summary>
    Empty,

    /// <summary>引数が空白だけの文字列</summary>
    WhiteSpace,

    /// <summary>引数と定数の比較が成り立つ</summary>
    Compare,
}

/// <summary>
/// 比較ガードの演算子（「引数 演算子 定数」が成り立つと例外になる）
/// </summary>
public enum CompareOperator
{
    /// <summary>==</summary>
    Equal,

    /// <summary>!=</summary>
    NotEqual,

    /// <summary>&lt;</summary>
    LessThan,

    /// <summary>&lt;=</summary>
    LessThanOrEqual,

    /// <summary>&gt;</summary>
    GreaterThan,

    /// <summary>&gt;=</summary>
    GreaterThanOrEqual,
}

/// <summary>
/// ガードが例外を投げる条件（引数1つに対する条件）
/// </summary>
public sealed class GuardCondition
{
    private GuardCondition(GuardConditionKind kind, CompareOperator compareOperator, decimal constant)
    {
        Kind = kind;
        Operator = compareOperator;
        Constant = constant;
    }

    /// <summary>引数がnullなら例外</summary>
    public static GuardCondition Null { get; } = new(GuardConditionKind.Null, default, 0);

    /// <summary>引数が空文字列なら例外</summary>
    public static GuardCondition Empty { get; } = new(GuardConditionKind.Empty, default, 0);

    /// <summary>引数が空白だけの文字列なら例外</summary>
    public static GuardCondition WhiteSpace { get; } = new(GuardConditionKind.WhiteSpace, default, 0);

    /// <summary>条件の種類</summary>
    public GuardConditionKind Kind { get; }

    /// <summary>比較の演算子（Compareのときのみ有効）</summary>
    public CompareOperator Operator { get; }

    /// <summary>比較の定数（Compareのときのみ有効）</summary>
    public decimal Constant { get; }

    /// <summary>
    /// 「引数 演算子 定数」が成り立つと例外になる条件を作る
    /// </summary>
    /// <param name="compareOperator">比較の演算子</param>
    /// <param name="constant">比較の定数</param>
    /// <returns>比較の条件</returns>
    public static GuardCondition Compare(CompareOperator compareOperator, decimal constant)
    {
        return new GuardCondition(GuardConditionKind.Compare, compareOperator, constant);
    }

    /// <summary>
    /// 値がこの条件に当てはまる（ガードで例外になる）かを判定する
    /// </summary>
    /// <param name="value">判定する値</param>
    /// <returns>例外になるならtrue</returns>
    public bool IsViolatedBy(TestValue value)
    {
        return Kind switch
        {
            GuardConditionKind.Null => value.Kind == TestValueKind.Null,
            GuardConditionKind.Empty => value.Kind == TestValueKind.String && value.Text.Length == 0,
            GuardConditionKind.WhiteSpace => value.Kind == TestValueKind.String && 0 < value.Text.Length && value.Text.All(char.IsWhiteSpace),
            _ => value.Kind == TestValueKind.Number && Holds(value.Number),
        };
    }

    /// <summary>
    /// この条件に当てはまる代表値（境界の値）を返す
    /// </summary>
    /// <returns>例外になる値</returns>
    public TestValue GetViolatingValue()
    {
        return Kind switch
        {
            GuardConditionKind.Null => TestValue.Null,
            GuardConditionKind.Empty => TestValue.FromString(string.Empty),
            GuardConditionKind.WhiteSpace => TestValue.FromString(" "),
            _ => TestValue.FromNumber(Operator switch
            {
                CompareOperator.LessThan => Constant - 1,
                CompareOperator.GreaterThan or CompareOperator.NotEqual => Constant + 1,
                _ => Constant,
            }),
        };
    }

    /// <summary>
    /// 「値 演算子 定数」が成り立つかを判定する
    /// </summary>
    /// <param name="value">判定する値</param>
    /// <returns>成り立つならtrue</returns>
    private bool Holds(decimal value)
    {
        return Operator switch
        {
            CompareOperator.Equal => value == Constant,
            CompareOperator.NotEqual => value != Constant,
            CompareOperator.LessThan => value < Constant,
            CompareOperator.LessThanOrEqual => value <= Constant,
            CompareOperator.GreaterThan => Constant < value,
            _ => Constant <= value,
        };
    }
}
