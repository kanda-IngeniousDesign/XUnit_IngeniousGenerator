using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// 対象クラスからxUnitのテストクラスを生成する
/// </summary>
public static class TestClassGenerator
{
    private const string TestClassSuffix = "Tests";

    /// <summary>
    /// 対象クラスに対応するテストファイル1つ分のコードを生成する
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <returns>生成結果</returns>
    public static GenerationResult Generate(INamedTypeSymbol targetType)
    {
        var testClassName = targetType.Name + TestClassSuffix;

        // 文字列連結ではなくSyntaxFactoryで組み立て、整形はRoslynに任せる
        var testClass = ClassDeclaration(testClassName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword));

        var compilationUnit = CompilationUnit()
            .AddUsings(UsingDirective(ParseName("Xunit")))
            .AddMembers(WrapInNamespace(targetType, testClass))
            .NormalizeWhitespace();

        return new GenerationResult(testClassName, compilationUnit.ToFullString());
    }

    /// <summary>
    /// 対象クラスと同じ名前空間でテストクラスを包む（グローバル名前空間ならそのまま返す）
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="testClass">テストクラスの宣言</param>
    /// <returns>名前空間で包んだメンバー</returns>
    private static MemberDeclarationSyntax WrapInNamespace(INamedTypeSymbol targetType, ClassDeclarationSyntax testClass)
    {
        var ns = targetType.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace) return testClass;

        return FileScopedNamespaceDeclaration(ParseName(ns.ToDisplayString()))
            .AddMembers(testClass);
    }
}
