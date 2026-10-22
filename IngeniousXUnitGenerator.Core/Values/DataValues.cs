using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Values;

/// <summary>
/// テストデータとして渡す列挙型のメンバー
/// </summary>
public sealed class EnumMemberValue : IEquatable<EnumMemberValue>
{
    /// <summary>
    /// 列挙型のメンバーを作成する
    /// </summary>
    /// <param name="type">列挙型</param>
    /// <param name="name">メンバー名</param>
    public EnumMemberValue(ITypeSymbol type, string name)
    {
        Type = type;
        Name = name;
    }

    /// <summary>列挙型</summary>
    public ITypeSymbol Type { get; }

    /// <summary>メンバー名</summary>
    public string Name { get; }

    /// <summary>
    /// 同じ列挙型の同じメンバーかを判定する
    /// </summary>
    /// <param name="other">比べる値</param>
    /// <returns>同じならtrue</returns>
    public bool Equals(EnumMemberValue? other)
    {
        return other is not null && SymbolEqualityComparer.Default.Equals(Type, other.Type) && Name == other.Name;
    }

    /// <summary>
    /// 同じ値かを判定する
    /// </summary>
    /// <param name="obj">比べる値</param>
    /// <returns>同じならtrue</returns>
    public override bool Equals(object? obj)
    {
        return Equals(obj as EnumMemberValue);
    }

    /// <summary>
    /// ハッシュ値を返す
    /// </summary>
    /// <returns>ハッシュ値</returns>
    public override int GetHashCode()
    {
        return Name.GetHashCode();
    }

    /// <summary>
    /// 表示用の文字列を返す
    /// </summary>
    /// <returns>表示用の文字列</returns>
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// テストデータとして渡すコレクション（配列・List&lt;T&gt;・IEnumerable&lt;T&gt; など）
/// </summary>
public sealed class CollectionValue : IEquatable<CollectionValue>
{
    /// <summary>
    /// コレクションを作成する
    /// </summary>
    /// <param name="collectionType">引数のコレクション型</param>
    /// <param name="elementType">要素の型</param>
    /// <param name="items">要素（int / string などの値、または EnumMemberValue）</param>
    public CollectionValue(ITypeSymbol collectionType, ITypeSymbol elementType, IReadOnlyList<object?> items)
    {
        CollectionType = collectionType;
        ElementType = elementType;
        Items = items;
    }

    /// <summary>引数のコレクション型</summary>
    public ITypeSymbol CollectionType { get; }

    /// <summary>要素の型</summary>
    public ITypeSymbol ElementType { get; }

    /// <summary>要素</summary>
    public IReadOnlyList<object?> Items { get; }

    /// <summary>
    /// 同じコレクション型で、要素が同じかを判定する
    /// </summary>
    /// <param name="other">比べる値</param>
    /// <returns>同じならtrue</returns>
    public bool Equals(CollectionValue? other)
    {
        return other is not null && SymbolEqualityComparer.Default.Equals(CollectionType, other.CollectionType) && Items.SequenceEqual(other.Items);
    }

    /// <summary>
    /// 同じ値かを判定する
    /// </summary>
    /// <param name="obj">比べる値</param>
    /// <returns>同じならtrue</returns>
    public override bool Equals(object? obj)
    {
        return Equals(obj as CollectionValue);
    }

    /// <summary>
    /// ハッシュ値を返す
    /// </summary>
    /// <returns>ハッシュ値</returns>
    public override int GetHashCode()
    {
        return Items.Count;
    }

    /// <summary>
    /// 表示用の文字列を返す
    /// </summary>
    /// <returns>表示用の文字列</returns>
    public override string ToString()
    {
        return $"[{string.Join(", ", Items)}]";
    }
}

/// <summary>
/// テストデータ（境界値を変える引数）として扱える型を判定する
/// </summary>
public static class DataTypes
{
    // 配列のほか、配列・List<T> で渡せるコレクションのインターフェース
    private static readonly string[] CollectionTypeNames =
    [
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IEnumerable<T>",
        "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>",
    ];

    /// <summary>
    /// 境界値を計算・比較できる基本の型（int, long, double, decimal, bool, string）かを判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>基本の型ならtrue</returns>
    public static bool IsPrimitive(ITypeSymbol type)
    {
        return type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double
            or SpecialType.System_Decimal or SpecialType.System_Boolean or SpecialType.System_String;
    }

    /// <summary>
    /// 列挙型かを判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>列挙型ならtrue</returns>
    public static bool IsEnum(ITypeSymbol type)
    {
        return type.TypeKind == TypeKind.Enum;
    }

    /// <summary>
    /// テストデータとして渡せるコレクションなら、要素の型を返す（要素は基本の型か列挙型に限る）
    /// </summary>
    /// <param name="type">型</param>
    /// <param name="elementType">要素の型</param>
    /// <returns>テストデータとして渡せるコレクションならtrue</returns>
    public static bool TryGetCollectionElement(ITypeSymbol type, out ITypeSymbol elementType)
    {
        elementType = type switch
        {
            IArrayTypeSymbol { Rank: 1 } array => array.ElementType,
            INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
                when CollectionTypeNames.Contains(named.OriginalDefinition.ToDisplayString()) => named.TypeArguments[0],
            _ => null!,
        };
        return elementType is not null && (IsPrimitive(elementType) || IsEnum(elementType));
    }

    /// <summary>
    /// List&lt;T&gt; そのものかを判定する（それ以外のコレクションは配列で渡す）
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>List&lt;T&gt; ならtrue</returns>
    public static bool IsList(ITypeSymbol type)
    {
        return type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";
    }

    /// <summary>
    /// 属性（InlineData）の引数に書けない列（decimal・コレクション）を含むかを判定する
    /// </summary>
    /// <param name="types">列の型</param>
    /// <returns>含むならtrue</returns>
    public static bool RequiresTheoryData(IEnumerable<ITypeSymbol> types)
    {
        return types.Any(t => t.SpecialType == SpecialType.System_Decimal || TryGetCollectionElement(t, out _));
    }
}
