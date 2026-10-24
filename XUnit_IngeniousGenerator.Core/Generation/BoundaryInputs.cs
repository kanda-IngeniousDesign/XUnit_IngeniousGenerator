using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// テストの入力（境界値を変える引数の行と、固定で渡す引数の値）
/// </summary>
internal sealed class BoundaryInputs
{
    /// <summary>
    /// テストの入力を作成する
    /// </summary>
    /// <param name="dataParameters">境界値を変える引数（数値・文字列・bool）</param>
    /// <param name="fixedValues">固定で渡す引数の値（モックなど）</param>
    /// <param name="rows">境界値の行（dataParameters の順）</param>
    public BoundaryInputs(
        IReadOnlyList<IParameterSymbol> dataParameters, IReadOnlyDictionary<IParameterSymbol, TestValue> fixedValues, IReadOnlyList<object?[]> rows)
    {
        DataParameters = dataParameters;
        FixedValues = fixedValues;
        Rows = rows;
    }

    /// <summary>境界値を変える引数（数値・文字列・bool）</summary>
    public IReadOnlyList<IParameterSymbol> DataParameters { get; }

    /// <summary>固定で渡す引数の値（モックなど）</summary>
    public IReadOnlyDictionary<IParameterSymbol, TestValue> FixedValues { get; }

    /// <summary>境界値の行（DataParameters の順）</summary>
    public IReadOnlyList<object?[]> Rows { get; }
}
