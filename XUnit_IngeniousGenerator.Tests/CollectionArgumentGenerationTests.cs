using XUnit_IngeniousGenerator.Core;
using XUnit_IngeniousGenerator.Tests.Support;

namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// コレクションのインターフェースの引数に、モック（列挙すると NullReferenceException になる）ではなく空のコレクションを渡し、
/// 要素がクラス（DTO）のコレクションは要素1件の値も使うことを、生成したテストを実際にビルド・実行して確かめる
/// </summary>
public class CollectionArgumentGenerationTests
{
    private const string Source = """
        using System.Collections.Generic;
        namespace Sample;
        public class Order { public int Amount { get; set; } }
        public interface IRepo { IEnumerable<Order> All(); }
        public class Service
        {
            private readonly IEnumerable<Order> _seed;
            private readonly IRepo _repo;

            public Service(IEnumerable<Order> seed, IRepo repo)
            {
                _seed = seed;
                _repo = repo;
            }

            public void Touch()
            {
                foreach (var order in _seed) { _ = order.Amount; }
            }

            public void Visit(IEnumerable<Order> orders)
            {
                foreach (var order in orders) { _ = order.Amount; }
            }

            public void Save(IDictionary<string, int> map, IList<Order> orders)
            {
                foreach (var pair in map) { _ = pair.Value; }
                foreach (var order in orders) { _ = order.Amount; }
                _ = _repo;
            }
        }
        """;

    [Fact]
    public async Task Generate_CollectionInterfaces_PassesEmptyCollectionsInsteadOfMocks()
    {
        var result = TargetCompiler.Generate(Source, "Sample.Service", options: new GenerationOptions(useMoq: true, useVerify: false));
        var passed = await TargetCompiler.RunGeneratedTestsAsync(Source, result.Code);

        Assert.Contains("private readonly Service _sut = new Service(Array.Empty<Order>(), new Mock<IRepo>().Object);", result.Code);
        Assert.Contains("new Order[] { new Order { Amount = 1 } }", result.Code);
        Assert.Contains("new Dictionary<string, int>()", result.Code);
        Assert.DoesNotContain("Mock<IEnumerable", result.Code);
        Assert.DoesNotContain("Mock<IDictionary", result.Code);
        Assert.Equal(
            [
                "Touch_DefaultInputs_DoesNotThrow",
                "Visit_BoundaryValues_DoesNotThrow",
                "Save_BoundaryValues_DoesNotThrow",
            ],
            passed);
    }
}
