using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XUnit_IngeniousGenerator.Core.Values;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// テスト対象のインスタンスを作った直後の、フィールド・自動実装プロパティの値。
/// xUnit はテストごとにテストクラス（と _sut）を作り直すため、関数のテストは常にこの状態から始まる。
/// 値が分からないもの（モック・組み立てたオブジェクトを受け取ったフィールドなど）は「不明」として持つ
/// </summary>
public sealed class InstanceState
{
    /// <summary>値が分からないことを表す値</summary>
    public static readonly object Unknown = new();

    private readonly Dictionary<ISymbol, object?> _values;

    /// <summary>
    /// 状態を作成する
    /// </summary>
    /// <param name="type">テスト対象のクラス</param>
    /// <param name="values">フィールド・自動実装プロパティの値</param>
    private InstanceState(INamedTypeSymbol type, Dictionary<ISymbol, object?> values)
    {
        Type = type;
        _values = values;
    }

    /// <summary>テスト対象のクラス</summary>
    public INamedTypeSymbol Type { get; }

    /// <summary>
    /// フィールド・自動実装プロパティの初期値（初期化子、なければ型の既定値）で、状態を作る
    /// </summary>
    /// <param name="type">テスト対象のクラス</param>
    /// <returns>状態（値の分からないものは不明）</returns>
    public static InstanceState CreateInitial(INamedTypeSymbol type)
    {
        var values = new Dictionary<ISymbol, object?>(SymbolEqualityComparer.Default);
        foreach (var member in type.GetMembers().Where(IsStateMember))
        {
            values[member] = Unknown;
        }

        return new InstanceState(type, values);
    }

    /// <summary>
    /// 状態として持つメンバー（定数でないフィールドと、自動実装プロパティ）かを判定する
    /// </summary>
    /// <param name="member">メンバー</param>
    /// <returns>状態として持つならtrue</returns>
    public static bool IsStateMember(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol field => !field.IsConst && !field.IsImplicitlyDeclared,
            IPropertySymbol property => IsAutoProperty(property),
            _ => false,
        };
    }

    /// <summary>
    /// 自動実装プロパティ（{ get; set; } など、本体を持たないアクセサーだけのプロパティ）かを判定する
    /// </summary>
    /// <param name="property">プロパティ</param>
    /// <returns>自動実装プロパティならtrue</returns>
    public static bool IsAutoProperty(IPropertySymbol property)
    {
        return !property.IsIndexer && !property.IsAbstract && property.DeclaringSyntaxReferences.Length != 0
            && property.DeclaringSyntaxReferences.All(r => r.GetSyntax() is PropertyDeclarationSyntax
            {
                ExpressionBody: null,
                AccessorList: { } accessors,
            } && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null));
    }

    /// <summary>
    /// 型の既定値（int は 0、参照型は null など）を返す。評価で扱えない型は不明にする
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>既定値</returns>
    public static object? DefaultValue(ITypeSymbol type)
    {
        if (type.IsReferenceType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return null;
        if (type.TypeKind == TypeKind.Enum) return 0;

        return type.SpecialType switch
        {
            SpecialType.System_Int32 => 0,
            SpecialType.System_Int64 => 0L,
            SpecialType.System_Double => 0D,
            SpecialType.System_Decimal => 0M,
            SpecialType.System_Boolean => false,
            SpecialType.System_Char => '\0',
            _ => TemporalValues.KindOf(type) switch
            {
                TemporalKind.DateTime => default(DateTime),
                TemporalKind.TimeSpan => TimeSpan.Zero,
                TemporalKind.DateOnly => new DateOnlyValue(DateTime.MinValue),
                _ => Unknown,
            },
        };
    }

    /// <summary>
    /// テストに渡す値（コンストラクタの引数）を、評価用の値にする（モック・組み立てたオブジェクトなどは不明）
    /// </summary>
    /// <param name="value">テストに渡す値</param>
    /// <param name="type">引数の型</param>
    /// <returns>評価用の値</returns>
    public static object? FromTestValue(TestValue value, ITypeSymbol type)
    {
        return value.Kind switch
        {
            TestValueKind.Null => null,
            TestValueKind.String => value.Text,
            TestValueKind.False => false,
            TestValueKind.Data => value.Data,
            TestValueKind.EnumMember => new EnumMemberValue(value.Type!, value.Text),
            TestValueKind.Number => type.SpecialType switch
            {
                SpecialType.System_Int32 => (int)value.Number,
                SpecialType.System_Int64 => (long)value.Number,
                SpecialType.System_Double => (double)value.Number,
                SpecialType.System_Decimal => value.Number,
                _ => Unknown,
            },
            _ => Unknown,
        };
    }

    /// <summary>
    /// 値を読む
    /// </summary>
    /// <param name="member">フィールド・自動実装プロパティ</param>
    /// <param name="value">値</param>
    /// <returns>値が分かっていればtrue</returns>
    public bool TryGet(ISymbol member, out object? value)
    {
        if (_values.TryGetValue(member, out value) && !ReferenceEquals(value, Unknown)) return true;

        value = null;
        return false;
    }

    /// <summary>
    /// 状態として持つメンバーかを判定する
    /// </summary>
    /// <param name="member">メンバー</param>
    /// <returns>持っていればtrue</returns>
    public bool Contains(ISymbol member)
    {
        return _values.ContainsKey(member);
    }

    /// <summary>
    /// 値を書く（不明にする場合は Unknown を渡す）
    /// </summary>
    /// <param name="member">フィールド・自動実装プロパティ</param>
    /// <param name="value">値</param>
    public void Set(ISymbol member, object? value)
    {
        _values[member] = value;
    }

    /// <summary>
    /// すべての値を不明にする
    /// </summary>
    public void ForgetAll()
    {
        foreach (var member in _values.Keys.ToList()) _values[member] = Unknown;
    }

    /// <summary>
    /// 写しを作る（入力ごとの評価で、書き込みがほかの入力に影響しないようにする）
    /// </summary>
    /// <returns>写し</returns>
    public InstanceState Copy()
    {
        return new InstanceState(Type, new Dictionary<ISymbol, object?>(_values, SymbolEqualityComparer.Default));
    }
}
