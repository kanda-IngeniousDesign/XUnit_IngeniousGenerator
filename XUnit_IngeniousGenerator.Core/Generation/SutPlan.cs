using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// テスト対象のインスタンス（Sut）をどのコンストラクタ・引数で作るか
/// </summary>
internal sealed class SutPlan
{
    private SutPlan(IMethodSymbol? constructor, IReadOnlyList<(TestValue Value, ITypeSymbol Type)> arguments, string? blocker)
    {
        Constructor = constructor;
        Arguments = arguments;
        Blocker = blocker;
    }

    /// <summary>使うコンストラクタ（作れない場合はnull）</summary>
    public IMethodSymbol? Constructor { get; }

    /// <summary>コンストラクタに渡す値</summary>
    public IReadOnlyList<(TestValue Value, ITypeSymbol Type)> Arguments { get; }

    /// <summary>作れない理由（作れる場合はnull）</summary>
    public string? Blocker { get; }

    /// <summary>
    /// ガードをすべて通過できるコンストラクタを、引数の少ない順に探す
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="context">生成の情報</param>
    /// <returns>Sutの作り方</returns>
    public static SutPlan Create(INamedTypeSymbol targetType, GenerationContext context)
    {
        if (targetType.IsStatic) return Blocked("staticクラスのため");
        if (targetType.IsAbstract) return Blocked("抽象クラスは具象サブクラスが必要なため");

        // null の依存で作ると、それを使う処理が NullReferenceException になるため、null を渡さずに作れる方法を優先する
        var solved = context.Constructor.Solve(targetType, allowNull: false);
        if (solved is not null) return new SutPlan(solved.Value.Constructor, solved.Value.Arguments, null);

        solved = context.Constructor.Solve(targetType, allowNull: true);
        if (solved is not null)
        {
            var nullParameters = solved.Value.Constructor.Parameters
                .Where((p, i) => solved.Value.Arguments[i].Value.Kind == TestValueKind.Null && p.NullableAnnotation != NullableAnnotation.Annotated)
                .Select(p => p.Name);
            context.ManualItems.Add($"{targetType.Name}: 引数 {string.Join(", ", nullParameters)} を作れないため null を渡してテスト対象を生成しています（それを使う処理のテストは例外になります）");
            return new SutPlan(solved.Value.Constructor, solved.Value.Arguments, null);
        }

        return Blocked(context.Options.UseMoq
            ? "ガードを通過する引数を決められるpublicコンストラクタがないため"
            : "ガードを通過する引数を決められるpublicコンストラクタがないため。テストプロジェクトに Moq を追加すると、インターフェースの依存を自動でモックします");
    }

    /// <summary>
    /// 作れない理由だけを持つ計画を作る
    /// </summary>
    /// <param name="reason">作れない理由</param>
    /// <returns>Sutの作り方</returns>
    private static SutPlan Blocked(string reason)
    {
        return new SutPlan(null, [], reason);
    }
}
