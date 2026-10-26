using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 引数なしコンストラクタがない具象クラスの依存を、ガードを通過する値で入れ子に組み立てる
/// </summary>
public class DependencyConstructionGenerationTests
{
    [Fact]
    public async Task Generate_ConcreteDependencies_AreBuiltWithValuesPassingTheirGuards()
    {
        const string source = """
            using System;
            namespace Sample;
            public class TaxRate
            {
                public TaxRate(decimal rate)
                {
                    if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
                    Rate = rate;
                }
                public decimal Rate { get; }
            }
            public class Options
            {
                public Options(TaxRate tax, string name)
                {
                    ArgumentNullException.ThrowIfNull(tax);
                    ArgumentException.ThrowIfNullOrEmpty(name);
                    Tax = tax;
                    Name = name;
                }
                public TaxRate Tax { get; }
                public string Name { get; }
            }
            public class Checker
            {
                private readonly Options _options;
                public Checker(Options options)
                {
                    ArgumentNullException.ThrowIfNull(options);
                    _options = options;
                }
                public void Check(int count)
                {
                    // 依存が null なら NullReferenceException になる
                    var label = _options.Name + _options.Tax.Rate + count;
                    _ = label.Length;
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Checker");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("new Checker(new Options(new TaxRate(1", result.Code);
        Assert.Contains("Constructor_OptionsIsNull_ThrowsArgumentNullException", passed);
        Assert.Contains(passed, name => name.StartsWith("Check_", StringComparison.Ordinal));
        Assert.Empty(result.ManualItems);
    }

    [Fact]
    public void Generate_DependencyThatCannotBeBuilt_ReportsNullArgument()
    {
        const string source = """
            namespace Sample;
            public class Node
            {
                public Node(Link link) { Link = link; }
                public Link Link { get; }
            }
            public class Link
            {
                public Link(Node node) { Node = node; }
                public Node Node { get; }
                public int Twice(int x) => x * 2;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Link");

        Assert.Contains("Link: 引数 node を作れないため null を渡してテスト対象を生成しています（それを使う処理のテストは例外になります）", result.ManualItems);
    }

    [Fact]
    public async Task Generate_DependencyNotUsedByAnyMethod_PassesNullInsteadOfMock()
    {
        const string source = """
            namespace Sample;
            public interface ILogger { void Write(string message); }
            public class Calculator
            {
                private readonly ILogger _logger;
                public Calculator(ILogger logger) { _logger = logger; }
                public int Twice(int x) => x * 2;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Calculator", options: new XUnit_IngeniousGenerator.Core.GenerationOptions(useMoq: true, useVerify: true));
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("new Calculator(null!)", result.Code);
        Assert.DoesNotContain("Mock", result.Code);
        Assert.Contains(passed, name => name.StartsWith("Twice_", StringComparison.Ordinal));
        Assert.Empty(result.ManualItems);
    }

    [Theory]
    [InlineData("public int Twice(int x) { _logger.Write(\"x\"); return x * 2; }", "_logger = logger;")]
    [InlineData("public int Twice(int x) => x * 2;", "System.ArgumentNullException.ThrowIfNull(logger); _logger = logger;")]
    [InlineData("public int Twice(int x) => x * 2;", "logger.Write(\"created\"); _logger = logger;")]
    public void Generate_DependencyUsedOrGuarded_KeepsMock(string method, string constructorBody)
    {
        var source = $$"""
            namespace Sample;
            public interface ILogger { void Write(string message); }
            public class Calculator
            {
                private readonly ILogger _logger;
                public Calculator(ILogger logger) { {{constructorBody}} }
                {{method}}
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Calculator", options: new XUnit_IngeniousGenerator.Core.GenerationOptions(useMoq: true, useVerify: true));

        Assert.Contains("new Calculator(new Mock<ILogger>().Object)", result.Code);
    }
}
