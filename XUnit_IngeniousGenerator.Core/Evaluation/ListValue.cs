using Microsoft.CodeAnalysis;
using XUnit_IngeniousGenerator.Core.Values;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// 評価中の List&lt;T&gt;・配列・列挙（LINQ の結果）の値。要素を書き換えられるため、入力ごとの評価では写しを使う
/// </summary>
internal sealed class ListValue
{
    /// <summary>
    /// 値を作成する
    /// </summary>
    /// <param name="elementType">要素の型（分からなければnull）</param>
    /// <param name="items">要素</param>
    /// <param name="isArray">配列か（範囲外の添字で出る例外が List と違う）</param>
    public ListValue(ITypeSymbol? elementType, IEnumerable<object?> items, bool isArray = false)
    {
        ElementType = elementType;
        Items = items.ToList();
        IsArray = isArray;
    }

    /// <summary>要素の型</summary>
    public ITypeSymbol? ElementType { get; }

    /// <summary>要素</summary>
    public List<object?> Items { get; }

    /// <summary>配列か</summary>
    public bool IsArray { get; }

    /// <summary>
    /// 写しを作る
    /// </summary>
    /// <returns>写し</returns>
    public ListValue Copy()
    {
        return new ListValue(ElementType, Items, IsArray);
    }

    /// <summary>
    /// テストに書ける値（コレクションの値）にする。要素が基本の型・列挙型のものだけ
    /// </summary>
    /// <param name="collectionType">戻り値のコレクションの型</param>
    /// <param name="elementType">要素の型</param>
    /// <param name="toExpected">要素を期待値に書ける値にする処理（列挙型は整数からメンバーに戻すなど）</param>
    /// <returns>コレクションの値（書けない要素があればnull）</returns>
    public CollectionValue? ToCollectionValue(ITypeSymbol collectionType, ITypeSymbol elementType, Func<ITypeSymbol, object?, object?> toExpected)
    {
        var items = new List<object?>(Items.Count);
        foreach (var item in Items)
        {
            var expected = toExpected(elementType, item);
            if (expected is null && item is not null) return null;
            items.Add(expected);
        }

        return new CollectionValue(collectionType, elementType, items);
    }
}
