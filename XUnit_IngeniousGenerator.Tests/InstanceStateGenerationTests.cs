using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// フィールド・プロパティを使う関数の期待値を、テスト対象のインスタンスを作った直後の状態から静的に計算し、
/// 生成したテストを実際にビルド・実行して成功することまで確かめる
/// </summary>
public class InstanceStateGenerationTests
{
    [Fact]
    public async Task Generate_FieldsAndProperties_TestsPassAgainstTarget()
    {
        const string source = """
            using System;
            namespace Sample;
            public class PriceService
            {
                private readonly decimal _taxRate;
                private readonly string _currency = "JPY";
                private int _count;

                public PriceService(decimal taxRate)
                {
                    if (taxRate < 0) throw new ArgumentOutOfRangeException(nameof(taxRate));
                    _taxRate = taxRate;
                }

                public decimal TaxRate => _taxRate;

                public decimal WithTax(decimal price) => price * (1 + _taxRate);

                public string Label(int price) => $"{price} {_currency}";

                public int Next()
                {
                    _count += 1;
                    return _count;
                }

                public bool IsTaxed() => 0 < TaxRate;
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.PriceService");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        Assert.Contains("[InlineData(1, \"1 JPY\")]", result.Code);
        Assert.Equal(
            [
                "Constructor_TaxRateIsMinus1_ThrowsArgumentOutOfRangeException",
                "WithTax_BoundaryValues_ReturnsExpected",
                "Label_BoundaryValues_ReturnsExpected",
                "Next_BoundaryValues_ReturnsExpected",
                "IsTaxed_BoundaryValues_ReturnsExpected",
            ],
            passed);
    }

    [Fact]
    public void Generate_MockedDependency_ComputesPropertiesButSnapshotsMockCalls()
    {
        const string source = """
            namespace Sample;
            public interface IRates { decimal Rate(string currency); }
            public class Greeter
            {
                private readonly IRates _rates;

                public Greeter(IRates rates) { _rates = rates; }

                public string Prefix { get; set; } = "Mr.";

                public string Hello(string name) => Prefix + " " + name;

                public decimal Convert(decimal amount) => amount * _rates.Rate("USD");
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Greeter", options: new GenerationOptions(useMoq: true, useVerify: true));

        // 自動実装プロパティの初期値は計算し、モックの呼び出しは記録（スナップショット）にする
        Assert.Contains("[InlineData(\"a\", \"Mr. a\")]", result.Code);
        Assert.Contains("// 戻り値を静的に計算できない理由: メソッド呼び出し '_rates.Rate'", result.Code);
    }
}
