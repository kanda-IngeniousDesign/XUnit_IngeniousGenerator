using Microsoft.CodeAnalysis;

namespace XUnit_IngeniousGenerator.Core.Generation;

/// <summary>
/// 関数1つ分の計画（生成するテストと、テストしていない部分の理由）。生成と警告の判定で同じ計画を使う
/// </summary>
internal sealed class MemberPlan
{
    private MemberPlan(IReadOnlyList<PlannedTest> tests, IReadOnlyList<string> manualItems, IReadOnlyList<string> notGeneratableItems, IReadOnlyList<string> reasons)
    {
        Tests = tests;
        ManualItems = manualItems;
        NotGeneratableItems = notGeneratableItems;
        Reasons = reasons;
    }

    /// <summary>生成するテスト</summary>
    public IReadOnlyList<PlannedTest> Tests { get; }

    /// <summary>要手動の項目（「関数名(引数): 理由」の形）</summary>
    public IReadOnlyList<string> ManualItems { get; }

    /// <summary>生成不可の項目（「関数名(引数): 理由」の形）</summary>
    public IReadOnlyList<string> NotGeneratableItems { get; }

    /// <summary>テストしていない部分の理由（関数名を除いたもの）</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>
    /// 関数1つ分のテストを計画する（理由をメンバーごとに分けるため、関数ごとに生成の情報を作る）
    /// </summary>
    /// <param name="member">関数</param>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="sut">テスト対象のインスタンスの作り方</param>
    /// <param name="compilation">テスト対象のクラスを含むコンパイル</param>
    /// <param name="options">生成の設定</param>
    /// <returns>計画</returns>
    public static MemberPlan Create(IMethodSymbol member, INamedTypeSymbol targetType, SutPlan sut, Compilation compilation, GenerationOptions options)
    {
        var display = MemberDisplay.Of(member);
        var outOfScope = TestTargets.GetOutOfScopeReason(member)
            ?? (targetType.IsGenericType ? "ジェネリッククラスは型引数を決められないため対象外です" : null);
        if (outOfScope is not null)
        {
            // 自動生成の対象外でも、フィールドを返すだけの get など処理のないものは、一覧に載せない
            return TestTargets.HasLogic(member) ? new MemberPlan([], [$"{display}: {outOfScope}"], [], [outOfScope]) : new MemberPlan([], [], [], []);
        }

        var context = new GenerationContext(compilation, options);
        var tests = new MemberTestPlanner(context, sut).Plan(member);
        var prefix = display + ": ";
        var reasons = context.NotGeneratableItems.Concat(context.ManualItems)
            .Select(r => r.StartsWith(prefix, StringComparison.Ordinal) ? r.Substring(prefix.Length) : r)
            .ToList();
        var manualItems = context.ManualItems.ToList();

        // 処理があるのにテストも理由もない（ガードのないコンストラクタ・set など）ものも、黙って外さない
        if (tests.Count == 0 && reasons.Count == 0 && TestTargets.HasLogic(member))
        {
            const string NoTest = "自動生成できるテストがありません（先頭のガードも戻り値もないため）";
            reasons.Add(NoTest);
            manualItems.Add($"{display}: {NoTest}");
        }

        return new MemberPlan(tests, manualItems, context.NotGeneratableItems, reasons);
    }
}
