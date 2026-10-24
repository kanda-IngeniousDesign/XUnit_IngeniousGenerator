using XUnit_IngeniousGenerator.Core.Guards;
using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Values;

/// <summary>
/// ガードを通過する値（有効な値）と、型に収まるかを判定する
/// </summary>
public static class ValueSolver
{
    /// <summary>
    /// 型に収まり、どの条件にも当てはまらない（例外にならない）値を探す
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="conditions">その引数に対する、先に評価されるガードの条件</param>
    /// <param name="allowMocks">インターフェース・抽象クラスを Moq でモックしてよいか</param>
    /// <param name="construct">引数なしコンストラクタがないクラスを、引数を決めて生成する方法（なければ生成しない）</param>
    /// <returns>有効な値（見つからなければnull）</returns>
    public static TestValue? FindValid(
        ITypeSymbol type, IReadOnlyList<GuardCondition> conditions, bool allowMocks, Func<INamedTypeSymbol, TestValue?>? construct = null)
    {
        return GetCandidates(type, conditions, allowMocks, construct)
            .FirstOrDefault(v => IsRepresentable(v, type) && !conditions.Any(c => c.IsViolatedBy(v)));
    }

    /// <summary>
    /// 値をその型の引数に渡せるかを判定する
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="type">引数の型</param>
    /// <returns>渡せるならtrue</returns>
    public static bool IsRepresentable(TestValue value, ITypeSymbol type)
    {
        return value.Kind switch
        {
            TestValueKind.Null => type.IsReferenceType || IsNullableValueType(type),
            TestValueKind.String => type.SpecialType == SpecialType.System_String,
            TestValueKind.Number => IsInRange(value.Number, type),
            _ => true,
        };
    }

    /// <summary>
    /// 型ごとの値の候補を、優先順に列挙する
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="conditions">その引数に対するガードの条件</param>
    /// <param name="allowMocks">インターフェース・抽象クラスを Moq でモックしてよいか</param>
    /// <param name="construct">引数なしコンストラクタがないクラスを生成する方法</param>
    /// <returns>値の候補</returns>
    private static IEnumerable<TestValue> GetCandidates(
        ITypeSymbol type, IReadOnlyList<GuardCondition> conditions, bool allowMocks, Func<INamedTypeSymbol, TestValue?>? construct)
    {
        var candidate = GetNonNullCandidates(type, conditions);
        if (construct is not null && type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named && !HasPublicParameterlessConstructor(named))
        {
            // 候補を列挙する時点までは組み立てない（前の候補で決まれば不要なため）
            candidate = candidate.Concat(new[] { named }.Select(construct).OfType<TestValue>());
        }

        if (allowMocks && IsMockable(type)) candidate = candidate.Append(TestValue.OfType(TestValueKind.Mock, type));
        return type.IsReferenceType || IsNullableValueType(type) ? candidate.Append(TestValue.Null) : candidate;
    }

    /// <summary>
    /// null 以外の値の候補を、優先順に列挙する
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <param name="conditions">その引数に対するガードの条件</param>
    /// <returns>値の候補</returns>
    private static IEnumerable<TestValue> GetNonNullCandidates(ITypeSymbol type, IReadOnlyList<GuardCondition> conditions)
    {
        if (type.SpecialType == SpecialType.System_String) return [TestValue.FromString("a")];
        if (ConditionParser.IsNumeric(type)) return GetNumberCandidates(conditions);
        if (type.SpecialType == SpecialType.System_Boolean) return [TestValue.False];
        if (type.SpecialType == SpecialType.System_Char) return [TestValue.Char];

        return type switch
        {
            IArrayTypeSymbol array => [TestValue.OfType(TestValueKind.EmptyArray, array)],
            INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType => GetEnumCandidates(enumType),
            INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named when HasPublicParameterlessConstructor(named) =>
                [TestValue.OfType(TestValueKind.New, named)],
            INamedTypeSymbol { TypeKind: TypeKind.Struct } named when !IsNullableValueType(named) => [TestValue.OfType(TestValueKind.Default, named)],
            _ => [],
        };
    }

    /// <summary>
    /// 数値の候補（0, 1, -1 と、ガードの定数の前後）を列挙する
    /// </summary>
    /// <param name="conditions">その引数に対するガードの条件</param>
    /// <returns>数値の候補</returns>
    private static IEnumerable<TestValue> GetNumberCandidates(IReadOnlyList<GuardCondition> conditions)
    {
        var constants = conditions.Where(c => c.Kind == GuardConditionKind.Compare).Select(c => c.Constant);
        return new decimal[] { 0, 1, -1 }
            .Concat(constants.SelectMany(c => new[] { c, c + 1, c - 1 }))
            .Distinct()
            .Select(TestValue.FromNumber);
    }

    /// <summary>
    /// 列挙型の最初のメンバーを候補にする
    /// </summary>
    /// <param name="enumType">列挙型</param>
    /// <returns>値の候補</returns>
    private static IEnumerable<TestValue> GetEnumCandidates(INamedTypeSymbol enumType)
    {
        var first = enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue);
        return first is null
            ? [TestValue.OfType(TestValueKind.Default, enumType)]
            : [TestValue.OfType(TestValueKind.EnumMember, enumType, first.Name)];
    }

    /// <summary>
    /// 数値がその数値型の範囲に収まるかを判定する
    /// </summary>
    /// <param name="number">数値</param>
    /// <param name="type">引数の型</param>
    /// <returns>収まるならtrue</returns>
    private static bool IsInRange(decimal number, ITypeSymbol type)
    {
        var isInteger = type.SpecialType is not (SpecialType.System_Decimal or SpecialType.System_Single or SpecialType.System_Double);
        if (isInteger && number != decimal.Truncate(number)) return false;

        return type.SpecialType switch
        {
            SpecialType.System_SByte => sbyte.MinValue <= number && number <= sbyte.MaxValue,
            SpecialType.System_Byte => byte.MinValue <= number && number <= byte.MaxValue,
            SpecialType.System_Int16 => short.MinValue <= number && number <= short.MaxValue,
            SpecialType.System_UInt16 => ushort.MinValue <= number && number <= ushort.MaxValue,
            SpecialType.System_Int32 => int.MinValue <= number && number <= int.MaxValue,
            SpecialType.System_UInt32 => uint.MinValue <= number && number <= uint.MaxValue,
            SpecialType.System_Int64 => long.MinValue <= number && number <= long.MaxValue,
            SpecialType.System_UInt64 => ulong.MinValue <= number && number <= ulong.MaxValue,
            SpecialType.System_Decimal or SpecialType.System_Single or SpecialType.System_Double => true,
            _ => false,
        };
    }

    /// <summary>
    /// Moq でモックできる型（インターフェース、または引数なしコンストラクタを持つ抽象クラス）かを判定する
    /// </summary>
    /// <param name="type">判定する型</param>
    /// <returns>モックできるならtrue</returns>
    private static bool IsMockable(ITypeSymbol type)
    {
        return type switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => true,
            INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: true, IsSealed: false } named =>
                named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected),
            _ => false,
        };
    }

    /// <summary>
    /// Nullable&lt;T&gt; かを判定する
    /// </summary>
    /// <param name="type">判定する型</param>
    /// <returns>Nullable&lt;T&gt; ならtrue</returns>
    private static bool IsNullableValueType(ITypeSymbol type)
    {
        return type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    /// <summary>
    /// public な引数なしコンストラクタを持つかを判定する
    /// </summary>
    /// <param name="type">判定する型</param>
    /// <returns>持つならtrue</returns>
    private static bool HasPublicParameterlessConstructor(INamedTypeSymbol type)
    {
        return type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
    }
}
