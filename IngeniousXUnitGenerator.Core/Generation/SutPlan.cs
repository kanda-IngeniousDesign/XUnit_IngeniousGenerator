using IngeniousXUnitGenerator.Core.Guards;
using IngeniousXUnitGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Generation;

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

        var constructors = targetType.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderBy(c => c.Parameters.Length);
        foreach (var constructor in constructors)
        {
            var arguments = TryCreateArguments(constructor, context);
            if (arguments is not null) return new SutPlan(constructor, arguments, null);
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

    /// <summary>
    /// コンストラクタのガードをすべて通過する引数を求める
    /// </summary>
    /// <param name="constructor">コンストラクタ</param>
    /// <param name="context">生成の情報</param>
    /// <returns>引数の値（ガードを解析できない、または有効な値がない場合はnull）</returns>
    private static List<(TestValue, ITypeSymbol)>? TryCreateArguments(IMethodSymbol constructor, GenerationContext context)
    {
        var conditions = new List<GuardCase>();
        var declaration = MemberDisplay.FindDeclaration(constructor);
        if (declaration is not null)
        {
            var guards = GuardExtractor.Extract(declaration, context.Compilation.GetSemanticModel(declaration.SyntaxTree), constructor);
            if (guards.Statements.Any(s => !s.IsRecognized)) return null;
            conditions.AddRange(guards.Statements.SelectMany(s => s.Cases));
        }

        var arguments = new List<(TestValue, ITypeSymbol)>(constructor.Parameters.Length);
        foreach (var parameter in constructor.Parameters)
        {
            var parameterConditions = conditions
                .Where(c => SymbolEqualityComparer.Default.Equals(c.Parameter, parameter))
                .Select(c => c.Condition)
                .ToList();
            var value = ValueSolver.FindValid(parameter.Type, parameterConditions, context.Options.UseMoq);
            if (value is null) return null;
            arguments.Add((value, parameter.Type));
        }

        return arguments;
    }
}
