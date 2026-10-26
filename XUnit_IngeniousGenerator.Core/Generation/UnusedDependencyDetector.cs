using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// コンストラクタで受け取るだけで、クラスのどこでも使われない依存を見つける
/// （テスト対象を作るときにモックを渡す必要がなく、null を渡せば済むため）
/// </summary>
internal static class UnusedDependencyDetector
{
    /// <summary>
    /// 引数が、null を渡しても例外にならない「使われない依存」かを判定する。
    /// null チェックがなく、コンストラクタでは単純にフィールド・プロパティへ代入するだけで、
    /// その代入先がクラスのほかの場所で一度も読まれていない場合に限る
    /// </summary>
    /// <param name="constructor">コンストラクタ</param>
    /// <param name="parameter">判定する引数</param>
    /// <param name="hasNullGuard">その引数に null チェックがあるか</param>
    /// <param name="compilation">コンパイル</param>
    /// <returns>使われない依存ならtrue</returns>
    public static bool IsUnused(IMethodSymbol constructor, IParameterSymbol parameter, bool hasNullGuard, Compilation compilation)
    {
        if (hasNullGuard || !IsDependencyType(parameter.Type)) return false;
        if (MemberDisplay.FindDeclaration(constructor) is not ConstructorDeclarationSyntax declaration) return false;
        if (declaration.Initializer is not null) return false;

        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        var targets = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var reference in FindReferences(declaration, model, parameter))
        {
            // 「フィールド = 引数;」の右辺以外で使っていれば、コンストラクタの中で null だと例外になりうる
            if (reference.Parent is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment
                || assignment.Right != reference
                || assignment.Parent is not ExpressionStatementSyntax
                || model.GetSymbolInfo(assignment.Left).Symbol is not ({ Kind: SymbolKind.Field or SymbolKind.Property } and var target)
                || !SymbolEqualityComparer.Default.Equals(target.ContainingType, constructor.ContainingType))
            {
                return false;
            }

            targets.Add(target);
        }

        return targets.All(t => !IsReadElsewhere(t, constructor.ContainingType, compilation));
    }

    /// <summary>
    /// モックや組み立てで作る依存の型（インターフェース・クラス）かを判定する（文字列・配列などのデータは除く）
    /// </summary>
    /// <param name="type">引数の型</param>
    /// <returns>依存の型ならtrue</returns>
    private static bool IsDependencyType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class } named
            && named.SpecialType == SpecialType.None
            && !Values.DataTypes.TryGetCollectionElement(named, out _);
    }

    /// <summary>
    /// 宣言の中で、指定のシンボルを指している識別子を列挙する
    /// </summary>
    /// <param name="node">探す範囲</param>
    /// <param name="model">セマンティックモデル</param>
    /// <param name="symbol">シンボル</param>
    /// <returns>識別子</returns>
    private static IEnumerable<IdentifierNameSyntax> FindReferences(SyntaxNode node, SemanticModel model, ISymbol symbol)
    {
        return node.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(n => n.Identifier.ValueText == symbol.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n).Symbol, symbol));
    }

    /// <summary>
    /// フィールド・プロパティが、代入以外（読み取り）でクラスのどこかから使われているかを判定する
    /// </summary>
    /// <param name="target">フィールド・プロパティ</param>
    /// <param name="type">クラス</param>
    /// <param name="compilation">コンパイル</param>
    /// <returns>使われていればtrue</returns>
    private static bool IsReadElsewhere(ISymbol target, INamedTypeSymbol type, Compilation compilation)
    {
        // public なプロパティは外から読まれうるため、使われていない依存とはみなさない
        if (target is IPropertySymbol { DeclaredAccessibility: not Accessibility.Private }) return true;

        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            var node = reference.GetSyntax();
            var model = compilation.GetSemanticModel(node.SyntaxTree);
            foreach (var use in FindReferences(node, model, target))
            {
                var isAssignmentTarget = use.Parent is AssignmentExpressionSyntax assignment && assignment.Left == use
                    || use.Parent is MemberAccessExpressionSyntax { Parent: AssignmentExpressionSyntax outer } access && outer.Left == access;
                if (!isAssignmentTarget) return true;
            }
        }

        return false;
    }
}
