using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// テストか「テストしていない一覧」（スキップのテスト）のどちらかに必ず載せる関数（処理を持つもの）を集め、テスト名の元になる名前を決める。
/// 自動生成の対象はこの一部で、残りは理由つきでスキップのテストにする
/// </summary>
public static class TestTargets
{
    /// <summary>テストしていない関数のスキップのテストの名前の末尾</summary>
    public const string NotTestedSuffix = "_NotTested";

    private static readonly MethodKind[] ListedKinds =
    [
        MethodKind.Ordinary, MethodKind.Constructor, MethodKind.StaticConstructor, MethodKind.PropertyGet, MethodKind.PropertySet,
        MethodKind.UserDefinedOperator, MethodKind.Conversion, MethodKind.ExplicitInterfaceImplementation,
    ];

    /// <summary>
    /// クラスと、その中の入れ子のクラスから、一覧の対象になる関数を集める
    /// （private の関数は、呼び出し元の関数のテストで確かめるため対象外。本体のない関数も対象外）
    /// </summary>
    /// <param name="type">テスト対象のクラス</param>
    /// <returns>関数</returns>
    public static IReadOnlyList<IMethodSymbol> Collect(INamedTypeSymbol type)
    {
        var members = type.GetMembers().OfType<IMethodSymbol>().Where(IsListed).ToList();
        foreach (var nested in type.GetTypeMembers().Where(t => t is { TypeKind: TypeKind.Class, IsRecord: false }))
        {
            members.AddRange(Collect(nested));
        }

        return members;
    }

    /// <summary>
    /// テスト名の元になる名前を返す（入れ子のクラスはクラス名を前に付け、オーバーロードは引数の型を後ろに付けて区別する）
    /// 例: Divide、Constructor、Total_Get、Inner_Calc、Add_Int_Int
    /// </summary>
    /// <param name="member">関数</param>
    /// <returns>テスト名の元になる名前</returns>
    public static string GetBaseName(IMethodSymbol member)
    {
        var name = GetCoreName(member);
        var overloaded = member.ContainingType.GetMembers().OfType<IMethodSymbol>()
            .Any(m => !SymbolEqualityComparer.Default.Equals(m, member) && IsListed(m) && GetCoreName(m) == name);
        if (overloaded)
        {
            name += member.Parameters.Length == 0 ? "_NoArgs" : "_" + string.Join("_", member.Parameters.Select(p => ToIdentifierPart(p.Type)));
        }

        for (var type = member.ContainingType; type.ContainingType is not null; type = type.ContainingType)
        {
            name = type.Name + "_" + name;
        }

        return name;
    }

    /// <summary>
    /// 自動生成の対象外となる理由を、関数の種類・アクセス修飾子から判定する（本体の中身による理由は生成の計画で判定する）
    /// </summary>
    /// <param name="member">関数</param>
    /// <returns>対象外の理由（自動生成を試せる場合はnull）</returns>
    public static string? GetOutOfScopeReason(IMethodSymbol member)
    {
        if (member.ContainingType.ContainingType is not null) return "入れ子のクラスは自動生成の対象外です";
        if (member.ContainingType.DeclaredAccessibility != Accessibility.Public)
        {
            return "public でないクラスは自動生成の対象外です（テストする場合は public にしてください）";
        }

        if (member.AssociatedSymbol is IPropertySymbol { IsIndexer: true }) return "インデクサーは自動生成の対象外です";

        return member.MethodKind switch
        {
            MethodKind.StaticConstructor => "静的コンストラクタは直接呼べないため対象外です",
            MethodKind.PropertyGet => "プロパティの get は自動生成の対象外です",
            MethodKind.UserDefinedOperator or MethodKind.Conversion => "演算子は自動生成の対象外です",
            MethodKind.ExplicitInterfaceImplementation => "インターフェースの明示的な実装は自動生成の対象外です",
            MethodKind.PropertySet when member.IsInitOnly => "init アクセサーはオブジェクト初期化子でしか呼べないため対象外です",
            _ when member.DeclaredAccessibility != Accessibility.Public => "public でない関数は自動生成の対象外です",
            _ => null,
        };
    }

    /// <summary>
    /// 本体に確かめる処理があるか（引数・定数をフィールドに代入する、フィールドを返すだけなら処理なし）を判定する
    /// </summary>
    /// <param name="member">関数</param>
    /// <returns>処理があればtrue</returns>
    public static bool HasLogic(IMethodSymbol member)
    {
        return GetBody(member) switch
        {
            ArrowExpressionClauseSyntax arrow => !IsTrivialExpression(arrow.Expression),
            BlockSyntax block => !block.Statements.All(IsTrivialStatement),
            ConstructorDeclarationSyntax constructor => HasLogic(constructor),
            _ => true,
        };
    }

    /// <summary>
    /// 一覧の対象（本体を持つ、private でない、処理を持ちうる種類の関数）かを判定する
    /// </summary>
    /// <param name="member">関数</param>
    /// <returns>対象ならtrue</returns>
    private static bool IsListed(IMethodSymbol member)
    {
        if (member.IsImplicitlyDeclared || member.IsAbstract || !ListedKinds.Contains(member.MethodKind)) return false;

        // インターフェースの明示的な実装は private だが、インターフェース経由で外から呼ばれる
        if (member.DeclaredAccessibility == Accessibility.Private && member.MethodKind != MethodKind.ExplicitInterfaceImplementation) return false;

        return GetBody(member) is not null;
    }

    /// <summary>
    /// 関数の本体（ブロック・式本体）を返す。コンストラクタは初期化子（base(...)）も見るため宣言そのものを返す
    /// </summary>
    /// <param name="member">関数</param>
    /// <returns>本体（自動実装のプロパティなど本体がなければnull）</returns>
    private static SyntaxNode? GetBody(IMethodSymbol member)
    {
        foreach (var syntax in member.DeclaringSyntaxReferences.Select(r => r.GetSyntax()))
        {
            switch (syntax)
            {
                case ConstructorDeclarationSyntax { Body: not null } or ConstructorDeclarationSyntax { ExpressionBody: not null }:
                    return syntax;
                case BaseMethodDeclarationSyntax method when method.Body is not null || method.ExpressionBody is not null:
                    return (SyntaxNode?)method.Body ?? method.ExpressionBody;
                case AccessorDeclarationSyntax accessor when accessor.Body is not null || accessor.ExpressionBody is not null:
                    return (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody;
                case ArrowExpressionClauseSyntax arrow:
                    return arrow;
            }
        }

        return null;
    }

    /// <summary>
    /// コンストラクタに処理があるか（初期化子の引数と本体がすべて単純な値の受け渡しなら処理なし）を判定する
    /// </summary>
    /// <param name="constructor">コンストラクタの宣言</param>
    /// <returns>処理があればtrue</returns>
    private static bool HasLogic(ConstructorDeclarationSyntax constructor)
    {
        if (constructor.Initializer is { } initializer && !initializer.ArgumentList.Arguments.All(a => IsTrivialExpression(a.Expression))) return true;
        if (constructor.ExpressionBody is { } arrow) return !IsTrivialExpression(arrow.Expression);
        return !constructor.Body!.Statements.All(IsTrivialStatement);
    }

    /// <summary>
    /// 処理のない文（単純な値の代入・return）かを判定する
    /// </summary>
    /// <param name="statement">文</param>
    /// <returns>処理がなければtrue</returns>
    private static bool IsTrivialStatement(StatementSyntax statement)
    {
        return statement switch
        {
            ExpressionStatementSyntax expression => IsTrivialExpression(expression.Expression),
            ReturnStatementSyntax { Expression: { } returned } => IsTrivialExpression(returned),
            _ => false,
        };
    }

    /// <summary>
    /// 処理のない式（変数・定数・this.x、それらの単純な代入）かを判定する
    /// </summary>
    /// <param name="expression">式</param>
    /// <returns>処理がなければtrue</returns>
    private static bool IsTrivialExpression(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax or LiteralExpressionSyntax => true,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => true,
            AssignmentExpressionSyntax assignment when assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression) =>
                IsTrivialExpression(assignment.Left) && IsTrivialExpression(assignment.Right),
            _ => false,
        };
    }

    /// <summary>
    /// 種類ごとの名前（オーバーロード・入れ子の区別をする前）を返す
    /// </summary>
    /// <param name="member">関数</param>
    /// <returns>名前</returns>
    private static string GetCoreName(IMethodSymbol member)
    {
        var property = member.AssociatedSymbol as IPropertySymbol;
        return member.MethodKind switch
        {
            MethodKind.Constructor => "Constructor",
            MethodKind.StaticConstructor => "StaticConstructor",
            MethodKind.PropertyGet => (property is { IsIndexer: true } ? "Indexer" : property?.Name ?? member.Name) + "_Get",

            // set アクセサーは、これまでどおりプロパティ名のまま（インデクサーは区別する）
            MethodKind.PropertySet => property is { IsIndexer: true } ? "Indexer_Set" : property?.Name ?? member.Name,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => "Operator" + member.Name.Replace("op_", string.Empty),
            MethodKind.ExplicitInterfaceImplementation => member.Name.Substring(member.Name.LastIndexOf('.') + 1),
            _ => member.Name,
        };
    }

    /// <summary>
    /// 型をテスト名に使える形にする（int → Int、int[] → IntArray、List&lt;string&gt; → ListOfString）
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>テスト名の一部</returns>
    private static string ToIdentifierPart(ITypeSymbol type)
    {
        var text = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            .Replace("[]", "Array").Replace("<", "Of").Replace("?", "Nullable");
        var cleaned = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "T" : char.ToUpperInvariant(cleaned[0]) + cleaned.Substring(1);
    }
}
