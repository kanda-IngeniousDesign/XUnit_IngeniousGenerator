using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// メンバーの表示名・宣言を扱う共通処理
/// </summary>
internal static class MemberDisplay
{
    /// <summary>
    /// レポート用の表示名を作る（例: Divide(int, int)）
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>表示名</returns>
    public static string Of(IMethodSymbol member)
    {
        if (member.MethodKind == MethodKind.PropertySet) return $"{GetName(member)}.set";
        if (member.MethodKind == MethodKind.PropertyGet) return $"{GetName(member)}.get";

        var parameters = string.Join(", ", member.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{GetName(member)}({parameters})";
    }

    /// <summary>
    /// テスト名に使うメンバー名を返す（コンストラクタは Constructor、set アクセサーはプロパティ名。オーバーロード・入れ子の区別は TestTargets.GetBaseName）
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ・set アクセサー</param>
    /// <returns>メンバー名</returns>
    public static string GetTestName(IMethodSymbol member)
    {
        return TestTargets.GetBaseName(member);
    }

    /// <summary>
    /// 本体（ブロックまたは式本体）を持つ宣言を探す
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>宣言（本体がない、またはソースがない場合はnull）</returns>
    public static BaseMethodDeclarationSyntax? FindDeclaration(IMethodSymbol member)
    {
        return member.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(d => d.Body is not null || d.ExpressionBody is not null);
    }

    /// <summary>
    /// 同じ名前・同じ引数の数のオーバーロードがあるか（引数にキャストを付けて呼び分ける必要があるか）を判定する
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ</param>
    /// <returns>あればtrue</returns>
    public static bool HasSameArityOverload(IMethodSymbol member)
    {
        return member.ContainingType.GetMembers(member.Name)
            .OfType<IMethodSymbol>()
            .Any(m => !SymbolEqualityComparer.Default.Equals(m, member) && m.Parameters.Length == member.Parameters.Length);
    }

    /// <summary>
    /// 表示用のメンバー名を返す（コンストラクタは型名、set アクセサーはプロパティ名）
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ・set アクセサー</param>
    /// <returns>メンバー名</returns>
    private static string GetName(IMethodSymbol member)
    {
        return member switch
        {
            { MethodKind: MethodKind.Constructor } => member.ContainingType.Name,
            { MethodKind: MethodKind.PropertySet or MethodKind.PropertyGet, AssociatedSymbol: { } property } => property.Name,
            _ => member.Name,
        };
    }
}
