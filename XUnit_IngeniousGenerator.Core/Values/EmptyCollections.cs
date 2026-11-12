using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Values;

/// <summary>
/// コレクションのインターフェース（IEnumerable&lt;T&gt;・IList&lt;T&gt;・IDictionary&lt;K, V&gt; など）に渡す、空のコレクションを決める。
/// Moq のモックは、何も設定しないと列挙（GetEnumerator）が null を返し、foreach で NullReferenceException になるため使わない
/// </summary>
public static class EmptyCollections
{
    // インターフェースの名前と、空のコレクションを作る具象型の名前（null は Array.Empty<T>()）
    private static readonly Dictionary<string, string?> ConcreteNames = new()
    {
        ["System.Collections.IEnumerable"] = null,
        ["System.Collections.Generic.IEnumerable<T>"] = null,
        ["System.Collections.Generic.IReadOnlyCollection<T>"] = null,
        ["System.Collections.Generic.IReadOnlyList<T>"] = null,
        ["System.Collections.Generic.ICollection<T>"] = "List",
        ["System.Collections.Generic.IList<T>"] = "List",
        ["System.Collections.Generic.ISet<T>"] = "HashSet",
        ["System.Collections.Generic.IReadOnlySet<T>"] = "HashSet",
        ["System.Collections.Generic.IDictionary<TKey, TValue>"] = "Dictionary",
        ["System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"] = "Dictionary",
    };

    /// <summary>
    /// 空のコレクションを渡せるインターフェースかを判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>渡せるならtrue</returns>
    public static bool IsSupported(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { TypeKind: TypeKind.Interface } named && ConcreteNames.ContainsKey(named.OriginalDefinition.ToDisplayString());
    }

    /// <summary>
    /// 空のコレクションを作る具象型の名前（List・HashSet・Dictionary）を返す
    /// </summary>
    /// <param name="type">コレクションのインターフェース</param>
    /// <returns>具象型の名前（Array.Empty&lt;T&gt;() で渡すものは null）</returns>
    public static string? ConcreteName(INamedTypeSymbol type)
    {
        return ConcreteNames.TryGetValue(type.OriginalDefinition.ToDisplayString(), out var name) ? name : null;
    }
}
