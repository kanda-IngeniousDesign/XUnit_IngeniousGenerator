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
    private const string XUnitNamespace = "Xunit";

    /// <summary>
    /// 対象クラスに対応するテストファイル1つ分のコードを生成する
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="testNamespace">テストクラスを置く名前空間（nullや空ならグローバル名前空間）</param>
    /// <returns>生成結果</returns>
    public static GenerationResult Generate(INamedTypeSymbol targetType, string? testNamespace)
    {
        var testClassName = targetType.Name + TestClassSuffix;

        // 文字列連結ではなくSyntaxFactoryで組み立て、整形はRoslynに任せる
        var testClass = ClassDeclaration(testClassName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword));

        var compilationUnit = CompilationUnit()
            .AddUsings(BuildUsings(targetType, testNamespace))
            .AddMembers(WrapInNamespace(testNamespace, testClass))
            .NormalizeWhitespace()
            .WithTrailingTrivia(CarriageReturnLineFeed);

        return new GenerationResult(testClassName, compilationUnit.ToFullString());
    }

    /// <summary>
    /// xUnitと、テスト対象の名前空間（テスト側から見えない場合のみ）のusingを作る
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="testNamespace">テストクラスを置く名前空間</param>
    /// <returns>名前順に並べたusingディレクティブ</returns>
    private static UsingDirectiveSyntax[] BuildUsings(INamedTypeSymbol targetType, string? testNamespace)
    {
        var namespaces = new List<string>(2) { XUnitNamespace };

        // テスト側の名前空間と同じか、その親の名前空間であれば、usingなしで参照できる
        var targetNamespace = targetType.ContainingNamespace;
        if (targetNamespace is not null && !targetNamespace.IsGlobalNamespace)
        {
            var target = targetNamespace.ToDisplayString();
            var test = testNamespace ?? string.Empty;
            if (test != target && !test.StartsWith(target + ".", StringComparison.Ordinal)) namespaces.Add(target);
        }

        return namespaces
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => UsingDirective(ParseName(n)))
            .ToArray();
    }

    /// <summary>
    /// 指定の名前空間でテストクラスを包む（名前空間が空ならそのまま返す）
    /// </summary>
    /// <param name="testNamespace">テストクラスを置く名前空間</param>
    /// <param name="testClass">テストクラスの宣言</param>
    /// <returns>名前空間で包んだメンバー</returns>
    private static MemberDeclarationSyntax WrapInNamespace(string? testNamespace, ClassDeclarationSyntax testClass)
    {
        if (string.IsNullOrEmpty(testNamespace)) return testClass;

        return FileScopedNamespaceDeclaration(ParseName(testNamespace!))
            .AddMembers(testClass);
    }
}
