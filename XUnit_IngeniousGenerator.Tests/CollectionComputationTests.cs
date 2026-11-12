using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// List・配列・ループ・LINQ を使う関数の期待値を静的に計算し、コレクションを空や要素の境界値に変えた入力で、
/// 生成したテストを実際にビルド・実行して成功することまで確かめる
/// </summary>
public class CollectionComputationTests
{
    [Fact]
    public async Task Generate_ListsLoopsAndLinq_TestsPassAgainstTarget()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            namespace Sample;
            public class Order { public int Amount { get; set; } public bool Paid { get; set; } }
            public class Report
            {
                public int Total(IEnumerable<Order> orders) { var sum = 0; foreach (var o in orders) sum += o.Amount; return sum; }
                public int PaidCount(IReadOnlyList<Order> orders) => orders.Count(o => o.Paid);
                public int Positives(int[] values) { var list = new List<int>(); foreach (var v in values) { if (v > 0) list.Add(v); } return list.Count; }
                public List<int> Doubled(List<int> values) => values.Select(v => v * 2).ToList();
                public int Factorial(int n) { var r = 1; for (var i = 2; i <= n; i++) r *= i; return r; }
                public bool HasLarge(IEnumerable<int> values) => values.Any(v => v >= 100);
                public int FirstLength(IList<string> names) => names.Count == 0 ? -1 : names[0].Length;
                public int SumAmount(List<Order> orders) => orders.Where(o => o.Paid).Sum(o => o.Amount);
            }
            """;

        var result = TargetCompiler.Generate(source, "Sample.Report");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(source, result.Code);

        // 要素と比べている定数（v >= 100）・読んでいるプロパティ（o.Paid）から、要素の境界値を作る
        Assert.Contains("{ new int[] { 99 }, false },", result.Code);
        Assert.Contains("{ new int[] { 100 }, true },", result.Code);
        Assert.Contains("{ new Order[] { new Order { Amount = 1, Paid = true } }, 1 }", result.Code);
        Assert.Contains("{ Array.Empty<string>(), -1 }", result.Code);
        Assert.Contains("{ new List<int> { 1 }, new List<int> { 2 } },", result.Code);
        Assert.Equal(
            [
                "Total_BoundaryValues_ReturnsExpected",
                "PaidCount_BoundaryValues_ReturnsExpected",
                "Positives_BoundaryValues_ReturnsExpected",
                "Doubled_BoundaryValues_ReturnsExpected",
                "Factorial_BoundaryValues_ReturnsExpected",
                "HasLarge_BoundaryValues_ReturnsExpected",
                "FirstLength_BoundaryValues_ReturnsExpected",
                "SumAmount_BoundaryValues_ReturnsExpected",
            ],
            passed.Where(n => !n.Contains("Throws")));
    }
}
