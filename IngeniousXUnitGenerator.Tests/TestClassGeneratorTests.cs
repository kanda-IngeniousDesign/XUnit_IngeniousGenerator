using IngeniousXUnitGenerator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IngeniousXUnitGenerator.Tests;

public class TestClassGeneratorTests
{
    [Fact]
    public void Generate_ClassInNamespace_EmitsTestClassInSameNamespace()
    {
        var targetType = GetType("namespace Sample.Domain; public class Foo { }", "Sample.Domain.Foo");

        var result = TestClassGenerator.Generate(targetType);

        Assert.Equal("FooTests", result.TestClassName);
        Assert.Equal(
            """
            using Xunit;

            namespace Sample.Domain;
            public class FooTests
            {
            }
            """.ReplaceLineEndings(),
            result.Code.ReplaceLineEndings());
    }

    [Fact]
    public void Generate_ClassInGlobalNamespace_EmitsTestClassWithoutNamespace()
    {
        var targetType = GetType("public class Foo { }", "Foo");

        var result = TestClassGenerator.Generate(targetType);

        Assert.DoesNotContain("namespace", result.Code);
        Assert.Contains("public class FooTests", result.Code);
    }

    private static INamedTypeSymbol GetType(string source, string metadataName)
    {
        var compilation = CSharpCompilation.Create(
            "Target",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        return compilation.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"{metadataName} が見つかりません。");
    }
}
