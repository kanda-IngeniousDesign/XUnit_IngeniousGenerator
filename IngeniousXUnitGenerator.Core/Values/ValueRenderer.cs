using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace IngeniousXUnitGenerator.Core.Values;

/// <summary>
/// テストの値・型をC#の構文に変換し、必要な名前空間を集める
/// </summary>
public sealed class ValueRenderer
{
    // 名前空間は using で補うため型名だけを出す（入れ子の型は外側の型名を付ける）
    private static readonly SymbolDisplayFormat TypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private readonly SortedSet<string> _namespaces = new(StringComparer.Ordinal);

    /// <summary>出力した型が属する名前空間</summary>
    public IReadOnlyCollection<string> Namespaces => _namespaces;

    /// <summary>
    /// 名前空間を using の対象に加える
    /// </summary>
    /// <param name="ns">名前空間</param>
    public void AddNamespace(string ns)
    {
        if (0 < ns.Length) _namespaces.Add(ns);
    }

    /// <summary>
    /// 型名の構文を作り、その型（型引数を含む）の名前空間を記録する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>型名の構文</returns>
    public TypeSyntax RenderType(ITypeSymbol type)
    {
        CollectNamespaces(type);
        return ParseTypeName(type.ToDisplayString(TypeFormat));
    }

    /// <summary>
    /// 値を、指定の型の引数に渡す式にする
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="type">引数の型</param>
    /// <param name="withCast">オーバーロードを確定させるため型へのキャストを付けるか</param>
    /// <returns>式の構文</returns>
    public ExpressionSyntax Render(TestValue value, ITypeSymbol type, bool withCast)
    {
        var expression = RenderValue(value, type);
        if (!withCast) return expression;

        return CastExpression(RenderType(type), expression);
    }

    /// <summary>
    /// 値を式にする
    /// </summary>
    /// <param name="value">値</param>
    /// <param name="type">引数の型</param>
    /// <returns>式の構文</returns>
    private ExpressionSyntax RenderValue(TestValue value, ITypeSymbol type)
    {
        return value.Kind switch
        {
            // null 非許容の引数にも渡せるよう、null 許容の警告を抑止する
            TestValueKind.Null => PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, LiteralExpression(SyntaxKind.NullLiteralExpression)),
            TestValueKind.String => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(value.Text)),
            TestValueKind.Number => RenderNumber(value.Number, type),
            TestValueKind.False => LiteralExpression(SyntaxKind.FalseLiteralExpression),
            TestValueKind.Char => LiteralExpression(SyntaxKind.CharacterLiteralExpression, Literal('a')),
            TestValueKind.EnumMember => MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, RenderType(value.Type!), IdentifierName(value.Text)),
            TestValueKind.New => ObjectCreationExpression(RenderType(value.Type!)).WithArgumentList(ArgumentList()),
            TestValueKind.Default => DefaultExpression(RenderType(value.Type!)),
            _ => RenderEmptyArray((IArrayTypeSymbol)value.Type!),
        };
    }

    /// <summary>
    /// 数値を、引数の型に合うリテラルにする（負数は単項マイナスを付ける）
    /// </summary>
    /// <param name="number">数値</param>
    /// <param name="type">引数の型</param>
    /// <returns>式の構文</returns>
    private static ExpressionSyntax RenderNumber(decimal number, ITypeSymbol type)
    {
        var magnitude = Math.Abs(number);
        var token = type.SpecialType switch
        {
            SpecialType.System_Decimal => Literal(magnitude),
            SpecialType.System_Double => Literal((double)magnitude),
            SpecialType.System_Single => Literal((float)magnitude),
            _ => IntegerLiteral(magnitude),
        };

        var literal = LiteralExpression(SyntaxKind.NumericLiteralExpression, token);
        return number < 0 ? PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, literal) : literal;
    }

    /// <summary>
    /// 整数のリテラルを接尾辞なしで作る（-2147483648 などの最小値も、C# の規則で元の型のまま書ける）
    /// </summary>
    /// <param name="magnitude">0以上の整数</param>
    /// <returns>リテラルのトークン</returns>
    private static SyntaxToken IntegerLiteral(decimal magnitude)
    {
        var text = magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (magnitude <= int.MaxValue) return Literal(text, (int)magnitude);
        if (magnitude <= uint.MaxValue) return Literal(text, (uint)magnitude);
        if (magnitude <= long.MaxValue) return Literal(text, (long)magnitude);
        return Literal(text, (ulong)magnitude);
    }

    /// <summary>
    /// 空の配列 Array.Empty&lt;T&gt;() を作る
    /// </summary>
    /// <param name="array">配列型</param>
    /// <returns>式の構文</returns>
    private ExpressionSyntax RenderEmptyArray(IArrayTypeSymbol array)
    {
        AddNamespace("System");
        var empty = GenericName("Empty").WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(RenderType(array.ElementType))));
        return InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("Array"), empty));
    }

    /// <summary>
    /// 型と型引数・配列の要素型が属する名前空間を記録する
    /// </summary>
    /// <param name="type">型</param>
    private void CollectNamespaces(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                CollectNamespaces(array.ElementType);
                return;
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments) CollectNamespaces(argument);
                break;
        }

        // int や string などのキーワード型と、int? と書ける Nullable<T> は using 不要
        if (IsKeywordType(type) || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return;
        if (type.ContainingNamespace is { IsGlobalNamespace: false } ns) AddNamespace(ns.ToDisplayString());
    }

    /// <summary>
    /// C# のキーワードで書ける型（int, string, object など）かを判定する
    /// </summary>
    /// <param name="type">判定する型</param>
    /// <returns>キーワードで書けるならtrue</returns>
    private static bool IsKeywordType(ITypeSymbol type)
    {
        return type.SpecialType is SpecialType.System_Object or SpecialType.System_Void or SpecialType.System_String
            or (>= SpecialType.System_Boolean and <= SpecialType.System_Double);
    }
}
