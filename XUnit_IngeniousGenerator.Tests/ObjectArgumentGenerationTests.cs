using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// クラス（DTO）の引数を、初期化子またはコンストラクタで組み立て、本体で読んでいるプロパティだけを境界値に変えて期待値を計算し、
/// 生成したテストを実際にビルド・実行して成功することまで確かめる
/// </summary>
public class ObjectArgumentGenerationTests
{
    private const string Source = """
        #nullable enable
        using System;
        namespace Sample;
        public enum Rank { Gold, Silver }
        public class Customer
        {
            public string Last { get; set; } = "";
            public string First { get; set; } = "";
            public int Age { get; set; }
            public Rank Rank { get; set; }
            public string FullName => Last + " " + First;
        }
        public class Point
        {
            public Point(int x, int y) { X = x; Y = y; }
            public int X { get; }
            public int Y { get; }
        }
        public class Shop
        {
            public string Format(Customer c) => c.Last + c.First;
            public bool IsAdult(Customer c) => c.Age >= 20;
            public decimal Price(Customer c, decimal price) => c.Rank == Rank.Gold ? price * 0.9m : price;
            public string Name(Customer c) => c.FullName;
            public int Sum(Point p) => p.X + p.Y;
            public string Greet(Customer? c) => c == null ? "guest" : c.First;
            public int Register(Customer c)
            {
                if (c.Age < 0) throw new ArgumentOutOfRangeException(nameof(c));
                return c.Age;
            }
        }
        """;

    [Fact]
    public async Task Generate_ObjectArguments_TestsPassAgainstTarget()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Shop");
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, result.Code);

        // 読んでいるプロパティ（Age）だけを、比べている定数の前後に変える
        Assert.Contains("{ new Customer { Last = \"a\", First = \"a\", Age = 19, Rank = Rank.Gold }, false },", result.Code);
        Assert.Contains("{ new Customer { Last = \"a\", First = \"a\", Age = 1, Rank = Rank.Silver }, 1M, 1M },", result.Code);
        Assert.Contains("{ new Point(-1, 1), 0 },", result.Code);
        Assert.Contains("{ null, \"guest\" }", result.Code);
        Assert.Equal(
            [
                "Format_BoundaryValues_ReturnsExpected",
                "IsAdult_BoundaryValues_ReturnsExpected",
                "Price_BoundaryValues_ReturnsExpected",
                "Name_BoundaryValues_ReturnsExpected",
                "Sum_BoundaryValues_ReturnsExpected",
                "Greet_BoundaryValues_ReturnsExpected",
                "Register_CAgeIsMinus1_ThrowsArgumentOutOfRangeException",
                "Register_BoundaryValues_ReturnsExpected",
            ],
            passed);
    }
}
