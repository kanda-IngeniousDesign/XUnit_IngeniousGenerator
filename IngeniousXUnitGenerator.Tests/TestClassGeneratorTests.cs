using IngeniousXUnitGenerator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IngeniousXUnitGenerator.Tests;

public class TestClassGeneratorTests
{
    [Fact]
    public void Generate_TestNamespaceDiffersFromTarget_AddsUsingForTargetNamespace()
    {
        var targetType = GetType("namespace Sample.Domain; public class Foo { }", "Sample.Domain.Foo");

        var result = TestClassGenerator.Generate(targetType, "Sample.Tests.Domain");

        Assert.Equal("FooTests", result.TestClassName);
        Assert.Equal(
            "using Sample.Domain;\r\nusing Xunit;\r\n\r\nnamespace Sample.Tests.Domain;\r\npublic class FooTests\r\n{\r\n}\r\n",
            result.Code);
    }

    [Theory]
    [InlineData("Sample.Domain")]
    [InlineData("Sample.Domain.Tests")]
    public void Generate_TargetNamespaceVisibleFromTestNamespace_OmitsUsing(string testNamespace)
    {
        var targetType = GetType("namespace Sample.Domain; public class Foo { }", "Sample.Domain.Foo");

        var result = TestClassGenerator.Generate(targetType, testNamespace);

        Assert.DoesNotContain("using Sample.Domain;", result.Code);
    }

    [Fact]
    public void Generate_NoTestNamespace_EmitsTestClassWithoutNamespace()
    {
        var targetType = GetType("public class Foo { }", "Foo");

        var result = TestClassGenerator.Generate(targetType, null);

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
