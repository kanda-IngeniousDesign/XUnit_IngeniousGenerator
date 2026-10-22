using IngeniousXUnitGenerator.Tests.Support;

namespace IngeniousXUnitGenerator.Tests;

/// <summary>
/// 戻り値がないメソッドに対する「境界値の入力で例外が出ないこと」のテストを生成し、実際に実行して確かめる
/// </summary>
public class NoThrowGenerationTests
{
    [Fact]
    public async Task Generate_VoidAndTaskMethods_ChecksNoThrowWithBoundaryInputs()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace Sample;
            public class Cart
            {
                private readonly List<decimal> _prices = new();

                public void Add(decimal price, int quantity)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(price);
                    if (quantity <= 0 || quantity > 99) throw new ArgumentOutOfRangeException(nameof(quantity));
                    for (var i = 0; i < quantity; i++) _prices.Add(price);
                }

                public void Clear() => _prices.Clear();

                public async Task SaveAsync(string name)
                {
                    ArgumentException.ThrowIfNullOrEmpty(name);
                    await Task.Yield();
                }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Cart");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("Add_BoundaryValues_DoesNotThrow", passed);
        Assert.Contains("Clear_DefaultInputs_DoesNotThrow", passed);
        Assert.Contains("SaveAsync_BoundaryValues_DoesNotThrow", passed);
        Assert.Contains("var exception = Record.Exception(() => _sut.Clear());", result.Code);
        Assert.Contains("var exception = await Record.ExceptionAsync(() => _sut.SaveAsync(name));", result.Code);
        Assert.Contains("Assert.Null(exception);", result.Code);

        // decimal は属性に書けないため TheoryData で渡し、ガードで例外になる値（-1, 0, 100 など）は入力に含めない
        Assert.Contains("public static TheoryData<decimal, int> Add_NoThrowInputs", result.Code);
        Assert.Contains("{ 1M, 99 }", result.Code);
        Assert.DoesNotContain("{ 1M, 100 }", result.Code);
        Assert.DoesNotContain("{ 1M, 0 }", result.Code);
        Assert.Empty(result.ManualItems);
    }
}
