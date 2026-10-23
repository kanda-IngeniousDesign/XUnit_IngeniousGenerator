using System.Globalization;
using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Values;

/// <summary>
/// テストで引数に渡す値の種類
/// </summary>
public enum TestValueKind
{
    /// <summary>null</summary>
    Null,

    /// <summary>文字列リテラル</summary>
    String,

    /// <summary>数値リテラル</summary>
    Number,

    /// <summary>false</summary>
    False,

    /// <summary>文字リテラル 'a'</summary>
    Char,

    /// <summary>列挙型の最初のメンバー</summary>
    EnumMember,

    /// <summary>コンストラクタでの生成（引数は Arguments）</summary>
    New,

    /// <summary>default(T)</summary>
    Default,

    /// <summary>空の配列</summary>
    EmptyArray,

    /// <summary>Moq のモック（new Mock&lt;T&gt;().Object）</summary>
    Mock,
}

/// <summary>
/// テストで引数に渡す値（型に依存しない形で持ち、出力時にC#の式にする）
/// </summary>
public sealed class TestValue
{
    private TestValue(TestValueKind kind, string text, decimal number, ITypeSymbol? type, IReadOnlyList<(TestValue Value, ITypeSymbol Type)>? arguments = null)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Type = type;
        Arguments = arguments ?? [];
    }

    /// <summary>null</summary>
    public static TestValue Null { get; } = new(TestValueKind.Null, string.Empty, 0, null);

    /// <summary>false</summary>
    public static TestValue False { get; } = new(TestValueKind.False, string.Empty, 0, null);

    /// <summary>'a'</summary>
    public static TestValue Char { get; } = new(TestValueKind.Char, string.Empty, 0, null);

    /// <summary>値の種類</summary>
    public TestValueKind Kind { get; }

    /// <summary>文字列の値（Stringのとき）・列挙メンバー名（EnumMemberのとき）</summary>
    public string Text { get; }

    /// <summary>数値の値（Numberのとき）</summary>
    public decimal Number { get; }

    /// <summary>生成する型（EnumMember・New・Default・EmptyArray・Mockのとき）</summary>
    public ITypeSymbol? Type { get; }

    /// <summary>コンストラクタに渡す値（Newのとき）</summary>
    public IReadOnlyList<(TestValue Value, ITypeSymbol Type)> Arguments { get; }

    /// <summary>
    /// テスト名の条件部分（例: IsNull, IsEmpty, IsMinus1）
    /// </summary>
    public string ConditionName => Kind switch
    {
        TestValueKind.Null => "IsNull",
        TestValueKind.String when Text.Length == 0 => "IsEmpty",
        TestValueKind.String => "IsWhiteSpace",
        TestValueKind.Number => "Is" + NumberToName(Number),
        _ => "IsInvalid",
    };

    /// <summary>
    /// 文字列の値を作る
    /// </summary>
    /// <param name="text">文字列</param>
    /// <returns>値</returns>
    public static TestValue FromString(string text)
    {
        return new TestValue(TestValueKind.String, text, 0, null);
    }

    /// <summary>
    /// 数値の値を作る
    /// </summary>
    /// <param name="number">数値</param>
    /// <returns>値</returns>
    public static TestValue FromNumber(decimal number)
    {
        return new TestValue(TestValueKind.Number, string.Empty, number, null);
    }

    /// <summary>
    /// 型を伴う値（列挙メンバー・new・default・空配列・モック）を作る
    /// </summary>
    /// <param name="kind">値の種類</param>
    /// <param name="type">生成する型</param>
    /// <param name="memberName">列挙メンバー名（EnumMemberのとき）</param>
    /// <returns>値</returns>
    public static TestValue OfType(TestValueKind kind, ITypeSymbol type, string memberName = "")
    {
        return new TestValue(kind, memberName, 0, type);
    }

    /// <summary>
    /// コンストラクタに引数を渡して生成する値を作る
    /// </summary>
    /// <param name="type">生成する型</param>
    /// <param name="arguments">コンストラクタに渡す値</param>
    /// <returns>値</returns>
    public static TestValue Construct(ITypeSymbol type, IReadOnlyList<(TestValue Value, ITypeSymbol Type)> arguments)
    {
        return new TestValue(TestValueKind.New, string.Empty, 0, type, arguments);
    }

    /// <summary>
    /// 数値をテスト名に使える文字列にする（0 → Zero, -1 → Minus1, 0.5 → 0Point5）
    /// </summary>
    /// <param name="number">数値</param>
    /// <returns>テスト名用の文字列</returns>
    private static string NumberToName(decimal number)
    {
        if (number == 0) return "Zero";

        // 末尾の 0 を落とす（1.50 → 1.5）
        var normalized = number / 1.0000000000000000000000000000m;
        var text = Math.Abs(normalized).ToString(CultureInfo.InvariantCulture).Replace(".", "Point");
        return number < 0 ? "Minus" + text : text;
    }
}
