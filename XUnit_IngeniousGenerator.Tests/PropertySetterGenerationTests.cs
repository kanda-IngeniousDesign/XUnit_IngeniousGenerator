using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// プロパティの set アクセサーの先頭のガードから例外テストを生成して実際に実行する
/// </summary>
public class PropertySetterGenerationTests
{
    [Fact]
    public async Task Generate_SetterGuards_ThrowsTestsPass()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Product
            {
                private int _stock;
                private string _name = "item";

                public int Stock
                {
                    get => _stock;
                    set
                    {
                        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                        _stock = value;
                    }
                }

                public string Name
                {
                    get => _name;
                    set => _name = value ?? throw new ArgumentNullException(nameof(value));
                }

                public string Note { get; set; } = "";
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Product");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("Stock_ValueIsMinus1_ThrowsArgumentOutOfRangeException", passed);
        Assert.Contains("Name_ValueIsNull_ThrowsArgumentNullException", passed);
        Assert.Contains("_sut.Stock = -1", result.Code);
        Assert.Contains("Assert.Equal(\"value\", exception.ParamName);", result.Code);
        Assert.DoesNotContain("Note", result.Code);
        Assert.Empty(result.ManualItems);
    }

    [Fact]
    public void Generate_InitOnlyOrPrivateSetter_IsNotTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class Settings
            {
                private int _a;
                private int _b;
                public int A { get => _a; init { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _a = value; } }
                public int B { get => _b; private set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _b = value; } }
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Settings");

        Assert.DoesNotContain("_Throws", result.Code);
    }
}
