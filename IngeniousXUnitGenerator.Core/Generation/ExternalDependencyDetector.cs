using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// 時刻・乱数・ファイル・DB・通信など、実行するたびに結果が変わる、または外部環境に依存する処理を見つける
/// </summary>
internal static class ExternalDependencyDetector
{
    // 実行するたびに結果が変わる型・メンバー（型名だけのものは、その型のどのメンバーでも該当）
    private static readonly string[] NonDeterministicMembers =
    [
        "System.DateTime.Now", "System.DateTime.UtcNow", "System.DateTime.Today",
        "System.DateTimeOffset.Now", "System.DateTimeOffset.UtcNow",
        "System.Guid.NewGuid", "System.Random", "System.Diagnostics.Stopwatch",
        "System.Environment.TickCount", "System.Environment.TickCount64",
    ];

    // 外部環境に依存する名前空間・型
    private static readonly string[] ExternalPrefixes =
    [
        "System.IO.File", "System.IO.Directory", "System.IO.FileStream", "System.IO.StreamReader", "System.IO.StreamWriter",
        "System.Net", "System.Data", "Microsoft.Data", "Microsoft.EntityFrameworkCore",
        "System.Console", "System.Diagnostics.Process", "System.Environment.GetEnvironmentVariable",
    ];

    /// <summary>
    /// メソッド本体が直接使っている、結果が安定しない処理を探す
    /// </summary>
    /// <param name="declaration">メソッドの宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <returns>見つかった理由（なければnull）</returns>
    public static string? Find(BaseMethodDeclarationSyntax declaration, SemanticModel model)
    {
        foreach (var name in declaration.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is null) continue;

            var fullName = GetFullName(symbol);
            if (NonDeterministicMembers.Any(m => Matches(fullName, m))) return $"'{name}' は実行するたびに結果が変わるため";
            if (ExternalPrefixes.Any(p => Matches(fullName, p))) return $"'{name}' は外部環境（ファイル・DB・通信など）に依存するため";
        }

        return null;
    }

    /// <summary>
    /// 型・メンバーの完全名を返す（メンバーは「型の完全名.メンバー名」）
    /// </summary>
    /// <param name="symbol">シンボル</param>
    /// <returns>完全名</returns>
    private static string GetFullName(ISymbol symbol)
    {
        return symbol switch
        {
            ITypeSymbol type => type.OriginalDefinition.ToDisplayString(),
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType.OriginalDefinition.ToDisplayString(),
            _ when symbol.ContainingType is not null => symbol.ContainingType.OriginalDefinition.ToDisplayString() + "." + symbol.Name,
            _ => symbol.ToDisplayString(),
        };
    }

    /// <summary>
    /// 完全名が指定の名前そのものか、その配下（名前空間・型のメンバー）かを判定する
    /// </summary>
    /// <param name="fullName">完全名</param>
    /// <param name="prefix">名前</param>
    /// <returns>該当すればtrue</returns>
    private static bool Matches(string fullName, string prefix)
    {
        return fullName == prefix || fullName.StartsWith(prefix + ".", StringComparison.Ordinal);
    }
}
