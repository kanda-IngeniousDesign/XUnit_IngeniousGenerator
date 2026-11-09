using XUnit_IngeniousGenerator.Core.Evaluation;
using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// テスト対象のインスタンス（Sut）をどのコンストラクタ・引数で作るか
/// </summary>
internal sealed class SutPlan
{
    private SutPlan(IMethodSymbol? constructor, IReadOnlyList<(TestValue Value, ITypeSymbol Type)> arguments, string? blocker, InstanceState? state = null)
    {
        Constructor = constructor;
        Arguments = arguments;
        Blocker = blocker;
        State = state;
    }

    /// <summary>使うコンストラクタ（作れない場合はnull）</summary>
    public IMethodSymbol? Constructor { get; }

    /// <summary>コンストラクタに渡す値</summary>
    public IReadOnlyList<(TestValue Value, ITypeSymbol Type)> Arguments { get; }

    /// <summary>作れない理由（作れる場合はnull）</summary>
    public string? Blocker { get; }

    /// <summary>この引数でインスタンスを作った直後の状態（求められなければnull）</summary>
    public InstanceState? State { get; }

    /// <summary>
    /// ガードをすべて通過できるコンストラクタを、引数の少ない順に探す
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="context">生成の情報</param>
    /// <returns>Sutの作り方</returns>
    public static SutPlan Create(INamedTypeSymbol targetType, GenerationContext context)
    {
        if (targetType.IsStatic) return Blocked(Messages.Get("SutStaticClass"));
        if (targetType.IsAbstract) return Blocked(Messages.Get("SutAbstractClass"));

        // null の依存で作ると、それを使う処理が NullReferenceException になるため、null を渡さずに作れる方法を優先する
        var solved = context.Constructor.Solve(targetType, allowNull: false);
        if (solved is not null) return Solved(targetType, solved.Value.Constructor, solved.Value.Arguments, context);

        solved = context.Constructor.Solve(targetType, allowNull: true);
        if (solved is not null)
        {
            var nullParameters = solved.Value.Constructor.Parameters
                .Where((p, i) => solved.Value.Arguments[i].Value.Kind == TestValueKind.Null && p.NullableAnnotation != NullableAnnotation.Annotated)
                .Select(p => p.Name);
            context.ManualItems.Add($"{targetType.Name}: {Messages.Get("SutNullArguments", string.Join(", ", nullParameters))}");
            return Solved(targetType, solved.Value.Constructor, solved.Value.Arguments, context);
        }

        return Blocked(context.Options.UseMoq
            ? Messages.Get("SutNoConstructor")
            : Messages.Get("SutNoConstructor") + Messages.Get("MoqHint"));
    }

    /// <summary>
    /// コンストラクタと引数が決まった計画を作り、インスタンスを作った直後の状態も求める
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="constructor">コンストラクタ</param>
    /// <param name="arguments">引数</param>
    /// <param name="context">生成の情報</param>
    /// <returns>計画</returns>
    private static SutPlan Solved(
        INamedTypeSymbol targetType, IMethodSymbol constructor, IReadOnlyList<(TestValue Value, ITypeSymbol Type)> arguments, GenerationContext context)
    {
        var values = arguments.Select(a => InstanceState.FromTestValue(a.Value, a.Type)).ToList();
        return new SutPlan(constructor, arguments, null, MethodInterpreter.CreateState(targetType, constructor, values, context.Compilation));
    }

    /// <summary>
    /// テスト対象を生成できない計画を作る
    /// </summary>
    /// <param name="reason">生成できない理由</param>
    /// <returns>計画</returns>
    private static SutPlan Blocked(string reason)
    {
        return new SutPlan(null, [], reason);
    }
}
