using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Values;

/// <summary>
/// テストデータとして渡すクラス（DTO）の値。
/// 初期化子で作る場合は new Customer { Last = "a", Age = 1 }、作れない場合は引数つきのコンストラクタで new Customer("a", 1) と書く
/// </summary>
public sealed class ObjectValue : IEquatable<ObjectValue>
{
    /// <summary>
    /// クラスの値を作成する
    /// </summary>
    /// <param name="type">クラス</param>
    /// <param name="constructor">使うコンストラクタ</param>
    /// <param name="arguments">コンストラクタの引数</param>
    /// <param name="properties">初期化子で設定するプロパティと値</param>
    public ObjectValue(INamedTypeSymbol type, IMethodSymbol constructor, IReadOnlyList<object?> arguments, IReadOnlyList<(IPropertySymbol Property, object? Value)> properties)
    {
        Type = type;
        Constructor = constructor;
        Arguments = arguments;
        Properties = properties;
    }

    /// <summary>クラス</summary>
    public INamedTypeSymbol Type { get; }

    /// <summary>使うコンストラクタ</summary>
    public IMethodSymbol Constructor { get; }

    /// <summary>コンストラクタの引数</summary>
    public IReadOnlyList<object?> Arguments { get; }

    /// <summary>初期化子で設定するプロパティと値</summary>
    public IReadOnlyList<(IPropertySymbol Property, object? Value)> Properties { get; }

    /// <summary>評価で使う、この値のフィールド・プロパティの状態（必要になったときに一度だけ求める）</summary>
    internal object? State { get; set; }

    /// <summary>
    /// プロパティを1つ変えた値を作る（初期化子で設定するプロパティ、なければ同じ名前のコンストラクタの引数を変える）
    /// </summary>
    /// <param name="property">プロパティ</param>
    /// <param name="value">新しい値</param>
    /// <returns>変えた値（変えられなければnull）</returns>
    public ObjectValue? With(IPropertySymbol property, object? value)
    {
        var index = Properties.ToList().FindIndex(p => SymbolEqualityComparer.Default.Equals(p.Property, property));
        if (0 <= index)
        {
            var properties = Properties.ToList();
            properties[index] = (property, value);
            return new ObjectValue(Type, Constructor, Arguments, properties);
        }

        var argument = Constructor.Parameters.ToList().FindIndex(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
        if (argument < 0) return null;

        var arguments = Arguments.ToList();
        arguments[argument] = value;
        return new ObjectValue(Type, Constructor, arguments, Properties);
    }

    /// <summary>
    /// 基準の値から変えたプロパティの名前と値を返す（テスト名に使う）
    /// </summary>
    /// <param name="baseValue">基準の値</param>
    /// <returns>変えたプロパティの名前と値（なければnull）</returns>
    public (string Name, object? Value)? DescribeChange(ObjectValue baseValue)
    {
        for (var i = 0; i < Properties.Count && i < baseValue.Properties.Count; i++)
        {
            if (!Equals(Properties[i].Value, baseValue.Properties[i].Value)) return (Properties[i].Property.Name, Properties[i].Value);
        }

        for (var i = 0; i < Arguments.Count && i < baseValue.Arguments.Count; i++)
        {
            if (!Equals(Arguments[i], baseValue.Arguments[i])) return (Constructor.Parameters[i].Name, Arguments[i]);
        }

        return null;
    }

    /// <summary>
    /// 同じクラスで、引数とプロパティの値が同じかを判定する
    /// </summary>
    /// <param name="other">比べる値</param>
    /// <returns>同じならtrue</returns>
    public bool Equals(ObjectValue? other)
    {
        return other is not null
            && SymbolEqualityComparer.Default.Equals(Type, other.Type)
            && Arguments.SequenceEqual(other.Arguments)
            && Properties.Select(p => p.Value).SequenceEqual(other.Properties.Select(p => p.Value));
    }

    /// <summary>
    /// 同じ値かを判定する
    /// </summary>
    /// <param name="obj">比べる値</param>
    /// <returns>同じならtrue</returns>
    public override bool Equals(object? obj)
    {
        return Equals(obj as ObjectValue);
    }

    /// <summary>
    /// ハッシュ値を返す
    /// </summary>
    /// <returns>ハッシュ値</returns>
    public override int GetHashCode()
    {
        return Type.Name.GetHashCode();
    }

    /// <summary>
    /// 表示用の文字列を返す
    /// </summary>
    /// <returns>表示用の文字列</returns>
    public override string ToString()
    {
        return $"{Type.Name} {{ {string.Join(", ", Properties.Select(p => $"{p.Property.Name} = {p.Value}"))} }}";
    }
}

/// <summary>
/// 本体で、クラス（DTO）の引数のプロパティを読んでいることを表す（境界値の定数と一緒に集める）
/// </summary>
public sealed class PropertyRead : IEquatable<PropertyRead>
{
    /// <summary>
    /// 読んでいるプロパティを記録する
    /// </summary>
    /// <param name="property">プロパティ</param>
    /// <param name="constant">比べている定数（比べていなければnull）</param>
    public PropertyRead(IPropertySymbol property, object? constant)
    {
        Property = property;
        Constant = constant;
    }

    /// <summary>プロパティ</summary>
    public IPropertySymbol Property { get; }

    /// <summary>比べている定数（比べていなければnull）</summary>
    public object? Constant { get; }

    /// <summary>
    /// 同じプロパティ・同じ定数かを判定する
    /// </summary>
    /// <param name="other">比べる値</param>
    /// <returns>同じならtrue</returns>
    public bool Equals(PropertyRead? other)
    {
        return other is not null && SymbolEqualityComparer.Default.Equals(Property, other.Property) && Equals(Constant, other.Constant);
    }

    /// <summary>
    /// 同じ値かを判定する
    /// </summary>
    /// <param name="obj">比べる値</param>
    /// <returns>同じならtrue</returns>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PropertyRead);
    }

    /// <summary>
    /// ハッシュ値を返す
    /// </summary>
    /// <returns>ハッシュ値</returns>
    public override int GetHashCode()
    {
        return Property.Name.GetHashCode();
    }
}

/// <summary>
/// テストデータとして組み立てられるクラス（DTO）かを判定し、組み立て方を決める
/// </summary>
public static class ObjectTypes
{
    /// <summary>
    /// 組み立て方を決める。引数なしのコンストラクタと、値を設定できる自動実装プロパティがあれば初期化子で、
    /// なければ、引数がすべて基本の型のコンストラクタ（引数の多いもの）で作る
    /// </summary>
    /// <param name="type">型</param>
    /// <param name="constructor">使うコンストラクタ</param>
    /// <param name="properties">初期化子で設定するプロパティ（コンストラクタで作る場合は空）</param>
    /// <returns>組み立てられるならtrue</returns>
    public static bool TryGetShape(ITypeSymbol type, out IMethodSymbol constructor, out IReadOnlyList<IPropertySymbol> properties)
    {
        constructor = null!;
        properties = [];
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false, IsStatic: false, IsGenericType: false } named) return false;
        if (named.SpecialType != SpecialType.None || TemporalValues.KindOf(named) != TemporalKind.None || DataTypes.TryGetCollectionElement(named, out _)) return false;
        if (!named.Locations.Any(l => l.IsInSource)) return false;

        var publicConstructors = named.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        var settable = named.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p is { IsStatic: false, DeclaredAccessibility: Accessibility.Public, SetMethod.DeclaredAccessibility: Accessibility.Public }
                && IsScalar(p.Type) && Evaluation.InstanceState.IsAutoProperty(p))
            .ToList();
        var parameterless = publicConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        if (parameterless is not null && 0 < settable.Count)
        {
            constructor = parameterless;
            properties = settable;
            return true;
        }

        var withArguments = publicConstructors.Where(c => 0 < c.Parameters.Length && c.Parameters.All(p => IsScalar(p.Type)))
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
        if (withArguments is null) return false;

        constructor = withArguments;
        return true;
    }

    /// <summary>
    /// プロパティ・コンストラクタの引数に使える、値を作れる型（基本の型・列挙型）かを判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>使えるならtrue</returns>
    public static bool IsScalar(ITypeSymbol type)
    {
        return DataTypes.IsPrimitive(type) || DataTypes.IsEnum(type);
    }
}
