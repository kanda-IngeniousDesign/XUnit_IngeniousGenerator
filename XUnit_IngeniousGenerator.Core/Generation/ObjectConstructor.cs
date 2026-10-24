using XUnit_IngeniousGenerator.Core.Guards;
using XUnit_IngeniousGenerator.Core.Values;
using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// コンストラクタのガードをすべて通過する引数を決めて、クラスのインスタンスの作り方を求める
/// （引数が具象クラスなら、同じ方法で入れ子に組み立てる）
/// </summary>
internal sealed class ObjectConstructor
{
    // 入れ子で組み立てる深さの上限（循環する依存で止まらなくなるのを防ぐ）
    private const int MaxDepth = 3;

    private readonly GenerationContext _context;

    /// <summary>
    /// 組み立ての準備をする
    /// </summary>
    /// <param name="context">生成の情報</param>
    public ObjectConstructor(GenerationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 引数として渡すインスタンスを組み立てる（ValueSolver から、引数なしコンストラクタがないクラスに使う）
    /// </summary>
    /// <param name="type">クラス</param>
    /// <returns>生成する値（組み立てられなければnull）</returns>
    public TestValue? CreateDependency(INamedTypeSymbol type)
    {
        return Create(type, 1);
    }

    /// <summary>
    /// ガードをすべて通過できるコンストラクタと引数を、引数の少ない順に探す
    /// </summary>
    /// <param name="type">クラス</param>
    /// <param name="allowNull">null 非許容の引数にも null を渡してよいか</param>
    /// <returns>コンストラクタと引数（見つからなければnull）</returns>
    public (IMethodSymbol Constructor, List<(TestValue Value, ITypeSymbol Type)> Arguments)? Solve(INamedTypeSymbol type, bool allowNull)
    {
        return Solve(type, 0, allowNull);
    }

    /// <summary>
    /// 指定の深さで、引数として渡すインスタンスを組み立てる
    /// （ガードの分からない外部の型は、引数の値で例外になるおそれがあるため、ソースにある public なクラスだけを対象にする）
    /// </summary>
    /// <param name="type">クラス</param>
    /// <param name="depth">入れ子の深さ</param>
    /// <returns>生成する値（組み立てられなければnull）</returns>
    private TestValue? Create(INamedTypeSymbol type, int depth)
    {
        if (MaxDepth < depth || type.IsGenericType || !IsVisibleFromTests(type)) return null;
        if (!type.Locations.Any(l => l.IsInSource)) return null;

        var solved = Solve(type, depth, allowNull: false);
        return solved is null ? null : TestValue.Construct(type, solved.Value.Arguments);
    }

    /// <summary>
    /// 指定の深さで、ガードをすべて通過できるコンストラクタと引数を探す
    /// </summary>
    /// <param name="type">クラス</param>
    /// <param name="depth">入れ子の深さ</param>
    /// <param name="allowNull">null 非許容の引数にも null を渡してよいか</param>
    /// <returns>コンストラクタと引数（見つからなければnull）</returns>
    private (IMethodSymbol Constructor, List<(TestValue Value, ITypeSymbol Type)> Arguments)? Solve(INamedTypeSymbol type, int depth, bool allowNull)
    {
        var constructors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderBy(c => c.Parameters.Length);
        foreach (var constructor in constructors)
        {
            var arguments = TryCreateArguments(constructor, depth, allowNull);
            if (arguments is not null) return (constructor, arguments);
        }

        return null;
    }

    /// <summary>
    /// コンストラクタのガードをすべて通過する引数を求める
    /// </summary>
    /// <param name="constructor">コンストラクタ</param>
    /// <param name="depth">入れ子の深さ</param>
    /// <param name="allowNull">null 非許容の引数にも null を渡してよいか</param>
    /// <returns>引数の値（ガードを解析できない、または有効な値がない場合はnull）</returns>
    private List<(TestValue, ITypeSymbol)>? TryCreateArguments(IMethodSymbol constructor, int depth, bool allowNull)
    {
        var conditions = new List<GuardCase>();
        var declaration = MemberDisplay.FindDeclaration(constructor);
        if (declaration is not null)
        {
            var guards = GuardExtractor.Extract(declaration, _context.Compilation.GetSemanticModel(declaration.SyntaxTree), constructor);
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
            var value = ValueSolver.FindValid(parameter.Type, parameterConditions, _context.Options.UseMoq, t => Create(t, depth + 1));
            if (value is null) return null;
            if (!allowNull && value.Kind == TestValueKind.Null && !AcceptsNull(parameter)) return null;
            arguments.Add((value, parameter.Type));
        }

        return arguments;
    }

    /// <summary>
    /// null を渡すことを想定した引数（? の注釈付き、または Nullable&lt;T&gt;）かを判定する
    /// </summary>
    /// <param name="parameter">引数</param>
    /// <returns>想定していればtrue</returns>
    private static bool AcceptsNull(IParameterSymbol parameter)
    {
        return parameter.NullableAnnotation == NullableAnnotation.Annotated
            || parameter.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    /// <summary>
    /// テストプロジェクトから参照できる（自身と外側の型がすべて public な）型かを判定する
    /// </summary>
    /// <param name="type">型</param>
    /// <returns>参照できればtrue</returns>
    private static bool IsVisibleFromTests(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
        }

        return true;
    }
}
