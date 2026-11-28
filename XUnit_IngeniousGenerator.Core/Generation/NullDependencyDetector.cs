using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XUnit_IngeniousGenerator.Core.Evaluation;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 作れずに null を渡した依存（モックなしのインターフェースなど）を、関数が使っているかを調べる
/// （使っていれば、テストを実行すると NullReferenceException になるため、テストを作らない）
/// </summary>
internal static class NullDependencyDetector
{
    /// <summary>
    /// 関数（と、そこから呼ぶ同じクラスの関数）が読む、null の依存のフィールド・プロパティを探す
    /// </summary>
    /// <param name="declaration">関数の宣言</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="instance">テスト対象を作った直後の状態</param>
    /// <param name="compilation">コンパイル</param>
    /// <returns>null の依存（使っていなければnull）</returns>
    public static ISymbol? FindUsed(BaseMethodDeclarationSyntax declaration, SemanticModel model, InstanceState? instance, Compilation compilation)
    {
        if (instance is null) return null;

        var visited = new HashSet<SyntaxNode>();
        var pending = new Stack<(SyntaxNode Node, SemanticModel Model)>();
        pending.Push((declaration, model));
        while (0 < pending.Count)
        {
            var (node, current) = pending.Pop();
            if (!visited.Add(node)) continue;

            foreach (var name in node.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = current.GetSymbolInfo(name).Symbol;
                if (symbol is null || !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, instance.Type)) continue;

                if (IsNullDependency(symbol, instance)) return symbol;

                // 同じクラスの関数を呼んでいれば、その中で使う依存も調べる
                if (symbol is IMethodSymbol method)
                {
                    foreach (var reference in method.DeclaringSyntaxReferences)
                    {
                        var callee = reference.GetSyntax();
                        pending.Push((callee, compilation.GetSemanticModel(callee.SyntaxTree)));
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// null が入った依存（null を受け付けない型のインターフェース・クラス）のフィールド・プロパティかを判定する
    /// </summary>
    /// <param name="symbol">フィールド・プロパティ</param>
    /// <param name="instance">テスト対象を作った直後の状態</param>
    /// <returns>null の依存ならtrue</returns>
    private static bool IsNullDependency(ISymbol symbol, InstanceState instance)
    {
        var (type, annotation) = symbol switch
        {
            IFieldSymbol field => (field.Type, field.NullableAnnotation),
            IPropertySymbol property => (property.Type, property.NullableAnnotation),
            _ => (null, NullableAnnotation.None),
        };
        return type is not null && annotation != NullableAnnotation.Annotated && UnusedDependencyDetector.IsDependencyType(type)
            && instance.TryGet(symbol, out var value) && value is null;
    }
}
