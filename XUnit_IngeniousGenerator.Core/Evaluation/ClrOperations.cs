using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XUnit_IngeniousGenerator.Core.Values;

namespace XUnit_IngeniousGenerator.Core.Evaluation;

/// <summary>
/// C# の演算と型変換を、実際の .NET の型で実行して再現する（int のオーバーフローやゼロ除算も実行時と同じになる）
/// </summary>
internal static class ClrOperations
{
    /// <summary>
    /// 値を指定の型に変換する（暗黙・明示の数値変換は unchecked の C# と同じ結果にする）
    /// </summary>
    /// <param name="value">変換する値</param>
    /// <param name="target">変換先の型</param>
    /// <returns>変換した値</returns>
    public static object? Convert(object? value, ITypeSymbol target)
    {
        if (target.SpecialType == SpecialType.System_Object) return value;
        if (target.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return Convert(value, ((INamedTypeSymbol)target).TypeArguments[0]);
        if (value is null)
        {
            return target.IsReferenceType ? null : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedNullConversion", target));
        }

        if (TemporalValues.KindOf(target) != TemporalKind.None && TemporalValues.IsTemporal(value)) return value;

        // 列挙型の値（date.DayOfWeek など）は、元になる整数のまま扱う
        if (target.TypeKind == TypeKind.Enum && value is int or long) return value;

        return target.SpecialType switch
        {
            SpecialType.System_Int32 => ToInt32(value),
            SpecialType.System_Int64 => ToInt64(value),
            SpecialType.System_Double => ToDouble(value),
            SpecialType.System_Decimal => ToDecimal(value),
            SpecialType.System_Boolean or SpecialType.System_String or SpecialType.System_Char when value.GetType() == ToClrType(target.SpecialType) => value,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedConversion", target)),
        };
    }

    /// <summary>
    /// 二項演算を実行する（オペランドは演算子の引数の型に変換済みであること）
    /// </summary>
    /// <param name="kind">二項演算の種類</param>
    /// <param name="operandType">演算子の引数の型（左辺）</param>
    /// <param name="left">左辺の値</param>
    /// <param name="right">右辺の値</param>
    /// <returns>演算結果</returns>
    public static object? Binary(SyntaxKind kind, ITypeSymbol operandType, object? left, object? right)
    {
        if (TemporalValues.KindOf(operandType) != TemporalKind.None) return TemporalMethods.Binary(kind, left, right);

        // 参照型と null の比較（customer == null など）は、null かどうかで決まる
        if (kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression && !operandType.IsValueType && (left is null || right is null)
            && operandType.SpecialType != SpecialType.System_String)
        {
            return kind == SyntaxKind.EqualsExpression == (left is null && right is null);
        }
        if (operandType.TypeKind == TypeKind.Enum && left is int leftNumber && right is int rightNumber) return Int32Binary(kind, leftNumber, rightNumber);
        if (operandType.TypeKind == TypeKind.Enum && left is long leftLong && right is long rightLong) return Int64Binary(kind, leftLong, rightLong);

        return operandType.SpecialType switch
        {
            SpecialType.System_Int32 => Int32Binary(kind, (int)left!, (int)right!),
            SpecialType.System_Int64 => Int64Binary(kind, (long)left!, (long)right!),
            SpecialType.System_Double => DoubleBinary(kind, (double)left!, (double)right!),
            SpecialType.System_Decimal => DecimalBinary(kind, (decimal)left!, (decimal)right!),
            SpecialType.System_Boolean => BooleanBinary(kind, (bool)left!, (bool)right!),
            SpecialType.System_String => StringBinary(kind, left, right),
            // string + object（"a" + 1 など）の連結だけを扱い、object どうしの参照比較は対象外にする
            SpecialType.System_Object when kind is SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression => StringBinary(kind, left, right),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedTypeOperation", operandType)),
        };
    }

    /// <summary>
    /// 単項演算（-x, +x, !x）を実行する
    /// </summary>
    /// <param name="kind">単項演算の種類</param>
    /// <param name="value">オペランドの値（演算子の引数の型に変換済み）</param>
    /// <returns>演算結果</returns>
    public static object Unary(SyntaxKind kind, object? value)
    {
        return (kind, value) switch
        {
            (SyntaxKind.LogicalNotExpression, bool b) => !b,
            (SyntaxKind.UnaryPlusExpression, not null) => value,
            (SyntaxKind.UnaryMinusExpression, int i) => unchecked(-i),
            (SyntaxKind.UnaryMinusExpression, long l) => unchecked(-l),
            (SyntaxKind.UnaryMinusExpression, double d) => -d,
            (SyntaxKind.UnaryMinusExpression, decimal m) => -m,
            (SyntaxKind.UnaryMinusExpression, TimeSpan t) => -t,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedUnary", kind)),
        };
    }

    /// <summary>
    /// 数値どうしを decimal で比較する（パターンの定数との比較用）
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="constant">定数</param>
    /// <returns>value が小さければ負、等しければ0、大きければ正</returns>
    public static int CompareNumbers(object value, object constant)
    {
        return ToComparable(value).CompareTo(ToComparable(constant));
    }

    /// <summary>
    /// 値が等しいかを判定する（数値は型をまたいで値で比較し、文字列は序数で比較する）
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="constant">定数</param>
    /// <returns>等しければtrue</returns>
    public static bool ValuesEqual(object? value, object? constant)
    {
        if (value is null || constant is null) return value is null && constant is null;
        if (IsNumber(value) && IsNumber(constant)) return CompareNumbers(value, constant) == 0;
        if (TemporalValues.IsTemporal(value)) return value.Equals(constant);
        return value is string or bool or char
            ? value.Equals(constant)
            : throw new UnsupportedSyntaxException(Messages.Get("UnsupportedComparison", value.GetType().Name));
    }

    /// <summary>
    /// 値が数値型（int, long, double, decimal）かを判定する
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>数値型ならtrue</returns>
    public static bool IsNumber(object? value)
    {
        return value is int or long or double or decimal;
    }

    /// <summary>
    /// int の二項演算（unchecked）
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="l">左辺</param>
    /// <param name="r">右辺</param>
    /// <returns>演算結果</returns>
    private static object Int32Binary(SyntaxKind kind, int l, int r)
    {
        return kind switch
        {
            SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression => unchecked(l + r),
            SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression => unchecked(l - r),
            SyntaxKind.MultiplyExpression or SyntaxKind.MultiplyAssignmentExpression => unchecked(l * r),
            SyntaxKind.DivideExpression or SyntaxKind.DivideAssignmentExpression => l / r,
            SyntaxKind.ModuloExpression or SyntaxKind.ModuloAssignmentExpression => l % r,
            _ => Compare(kind, l.CompareTo(r)),
        };
    }

    /// <summary>
    /// long の二項演算（unchecked）
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="l">左辺</param>
    /// <param name="r">右辺</param>
    /// <returns>演算結果</returns>
    private static object Int64Binary(SyntaxKind kind, long l, long r)
    {
        return kind switch
        {
            SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression => unchecked(l + r),
            SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression => unchecked(l - r),
            SyntaxKind.MultiplyExpression or SyntaxKind.MultiplyAssignmentExpression => unchecked(l * r),
            SyntaxKind.DivideExpression or SyntaxKind.DivideAssignmentExpression => l / r,
            SyntaxKind.ModuloExpression or SyntaxKind.ModuloAssignmentExpression => l % r,
            _ => Compare(kind, l.CompareTo(r)),
        };
    }

    /// <summary>
    /// double の二項演算
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="l">左辺</param>
    /// <param name="r">右辺</param>
    /// <returns>演算結果</returns>
    private static object DoubleBinary(SyntaxKind kind, double l, double r)
    {
        return kind switch
        {
            SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression => l + r,
            SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression => l - r,
            SyntaxKind.MultiplyExpression or SyntaxKind.MultiplyAssignmentExpression => l * r,
            SyntaxKind.DivideExpression or SyntaxKind.DivideAssignmentExpression => l / r,
            SyntaxKind.ModuloExpression or SyntaxKind.ModuloAssignmentExpression => l % r,
            // NaN を含む比較は CompareTo と C# の演算子で結果が異なるため、演算子で直接比べる
            SyntaxKind.EqualsExpression => l == r,
            SyntaxKind.NotEqualsExpression => l != r,
            SyntaxKind.LessThanExpression => l < r,
            SyntaxKind.LessThanOrEqualExpression => l <= r,
            SyntaxKind.GreaterThanExpression => r < l,
            SyntaxKind.GreaterThanOrEqualExpression => r <= l,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedOperation", kind)),
        };
    }

    /// <summary>
    /// decimal の二項演算（オーバーフロー・ゼロ除算は実行時と同じく例外になる）
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="l">左辺</param>
    /// <param name="r">右辺</param>
    /// <returns>演算結果</returns>
    private static object DecimalBinary(SyntaxKind kind, decimal l, decimal r)
    {
        return kind switch
        {
            SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression => l + r,
            SyntaxKind.SubtractExpression or SyntaxKind.SubtractAssignmentExpression => l - r,
            SyntaxKind.MultiplyExpression or SyntaxKind.MultiplyAssignmentExpression => l * r,
            SyntaxKind.DivideExpression or SyntaxKind.DivideAssignmentExpression => l / r,
            SyntaxKind.ModuloExpression or SyntaxKind.ModuloAssignmentExpression => l % r,
            _ => Compare(kind, l.CompareTo(r)),
        };
    }

    /// <summary>
    /// bool の二項演算
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="l">左辺</param>
    /// <param name="r">右辺</param>
    /// <returns>演算結果</returns>
    private static object BooleanBinary(SyntaxKind kind, bool l, bool r)
    {
        return kind switch
        {
            SyntaxKind.EqualsExpression => l == r,
            SyntaxKind.NotEqualsExpression => l != r,
            SyntaxKind.BitwiseAndExpression => l & r,
            SyntaxKind.BitwiseOrExpression => l | r,
            SyntaxKind.ExclusiveOrExpression => l ^ r,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedOperation", kind)),
        };
    }

    /// <summary>
    /// 文字列の連結・比較（== は序数比較）
    /// </summary>
    /// <param name="kind">演算の種類</param>
    /// <param name="l">左辺</param>
    /// <param name="r">右辺</param>
    /// <returns>演算結果</returns>
    private static object StringBinary(SyntaxKind kind, object? l, object? r)
    {
        return kind switch
        {
            SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression => ToConcatString(l) + ToConcatString(r),
            SyntaxKind.EqualsExpression => string.Equals(l as string, r as string, StringComparison.Ordinal),
            SyntaxKind.NotEqualsExpression => !string.Equals(l as string, r as string, StringComparison.Ordinal),
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedOperation", kind)),
        };
    }

    /// <summary>
    /// 比較結果（CompareTo の値）から、比較演算の結果を返す
    /// </summary>
    /// <param name="kind">比較の種類</param>
    /// <param name="comparison">CompareTo の結果</param>
    /// <returns>比較演算の結果</returns>
    private static object Compare(SyntaxKind kind, int comparison)
    {
        return kind switch
        {
            SyntaxKind.EqualsExpression => comparison == 0,
            SyntaxKind.NotEqualsExpression => comparison != 0,
            SyntaxKind.LessThanExpression => comparison < 0,
            SyntaxKind.LessThanOrEqualExpression => comparison <= 0,
            SyntaxKind.GreaterThanExpression => 0 < comparison,
            SyntaxKind.GreaterThanOrEqualExpression => 0 <= comparison,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedOperation", kind)),
        };
    }

    /// <summary>
    /// 文字列連結で使う文字列表現を返す（カルチャに依存する数値の書式は再現できないため対象外）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>文字列表現</returns>
    private static string ToConcatString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            char c => c.ToString(),
            bool b => b ? "True" : "False",
            int or long => System.Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedCultureConcat")),
        };
    }

    /// <summary>
    /// 数値を int に変換する（C# のキャストと同じ規則）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>変換した値</returns>
    private static object ToInt32(object value)
    {
        return value switch
        {
            int i => i,
            long l => unchecked((int)l),
            double d => unchecked((int)d),
            decimal m => (int)m,
            char c => (int)c,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedConversionFromTo", value.GetType().Name, "int")),
        };
    }

    /// <summary>
    /// 数値を long に変換する（C# のキャストと同じ規則）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>変換した値</returns>
    private static object ToInt64(object value)
    {
        return value switch
        {
            int i => (long)i,
            long l => l,
            double d => unchecked((long)d),
            decimal m => (long)m,
            char c => (long)c,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedConversionFromTo", value.GetType().Name, "long")),
        };
    }

    /// <summary>
    /// 数値を double に変換する（C# のキャストと同じ規則）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>変換した値</returns>
    private static object ToDouble(object value)
    {
        return value switch
        {
            int i => (double)i,
            long l => (double)l,
            double d => d,
            decimal m => (double)m,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedConversionFromTo", value.GetType().Name, "double")),
        };
    }

    /// <summary>
    /// 数値を decimal に変換する（C# のキャストと同じ規則）
    /// </summary>
    /// <param name="value">値</param>
    /// <returns>変換した値</returns>
    private static object ToDecimal(object value)
    {
        return value switch
        {
            int i => (decimal)i,
            long l => (decimal)l,
            double d => (decimal)d,
            decimal m => m,
            _ => throw new UnsupportedSyntaxException(Messages.Get("UnsupportedConversionFromTo", value.GetType().Name, "decimal")),
        };
    }

    /// <summary>
    /// 比較用に数値を decimal にする（範囲外の double は対象外）
    /// </summary>
    /// <param name="value">数値</param>
    /// <returns>decimal の値</returns>
    private static decimal ToComparable(object value)
    {
        try
        {
            return System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new UnsupportedSyntaxException(Messages.Get("UnsupportedDecimalComparison"));
        }
    }

    /// <summary>
    /// 型の種類に対応する .NET の型を返す
    /// </summary>
    /// <param name="specialType">型の種類</param>
    /// <returns>.NET の型</returns>
    private static Type? ToClrType(SpecialType specialType)
    {
        return specialType switch
        {
            SpecialType.System_Boolean => typeof(bool),
            SpecialType.System_String => typeof(string),
            SpecialType.System_Char => typeof(char),
            _ => null,
        };
    }
}
