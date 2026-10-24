using XUnit_IngeniousGenerator.Core.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 既存のテストと照らして、メンバーごとにテストがあるか、なければ自動で生成できるかを判定する
/// （アナライザーの警告に使うため、判定は実際の生成と同じ計画の処理で行う）
/// </summary>
public static class TestCoverageInspector
{
    /// <summary>
    /// テスト対象のメンバーごとに、テストの状態を判定する
    /// </summary>
    /// <param name="targetType">テスト対象のクラス</param>
    /// <param name="compilation">テスト対象のクラスを含むコンパイル</param>
    /// <param name="options">テストプロジェクトで使えるパッケージに応じた設定</param>
    /// <param name="existingTestNames">既存のテストクラスのメンバー名（テストファイルがなければ空）</param>
    /// <returns>メンバーごとのテストの状態</returns>
    public static IReadOnlyList<MemberCoverage> Inspect(
        INamedTypeSymbol targetType, Compilation compilation, GenerationOptions options, IReadOnlyCollection<string> existingTestNames)
    {
        var members = targetType.GetMembers().OfType<IMethodSymbol>().Where(TestClassGenerator.IsTestTarget).ToList();
        var uncovered = members.Where(m => !IsCovered(m, existingTestNames)).ToList();
        var results = members.Where(m => IsCovered(m, existingTestNames))
            .Select(m => new MemberCoverage(m, CoverageStatus.Covered, []))
            .ToList();
        if (uncovered.Count == 0) return results;

        if (targetType.IsGenericType)
        {
            results.AddRange(uncovered.Select(m => new MemberCoverage(m, CoverageStatus.NotGeneratable, ["ジェネリッククラスは型引数を決められないため対象外です"])));
            return results;
        }

        var sut = SutPlan.Create(targetType, new GenerationContext(compilation, options));
        foreach (var member in uncovered)
        {
            // 理由をメンバーごとに分けるため、メンバーごとに生成の情報を作る
            var context = new GenerationContext(compilation, options);
            var tests = new MemberTestPlanner(context, sut).Plan(member);
            results.Add(Classify(member, tests.Count, context));
        }

        return results;
    }

    /// <summary>
    /// テストファイルのコードから、テストクラスのメンバー名を集める
    /// </summary>
    /// <param name="testFileCode">テストファイルのコード</param>
    /// <param name="testClassName">テストクラス名（例: FooTests）</param>
    /// <returns>メンバー名（テストクラスがなければ空）</returns>
    public static IReadOnlyCollection<string> CollectTestNames(string testFileCode, string testClassName)
    {
        var root = CSharpSyntaxTree.ParseText(testFileCode).GetRoot();
        return root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == testClassName)
            .SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>())
            .Select(m => m.Identifier.ValueText)
            .ToList();
    }

    /// <summary>
    /// メンバーのテスト（命名規約 Method_Condition_Expected で、メンバー名_ で始まるもの）があるかを判定する
    /// </summary>
    /// <param name="member">メンバー</param>
    /// <param name="existingTestNames">既存のテストクラスのメンバー名</param>
    /// <returns>あればtrue</returns>
    private static bool IsCovered(IMethodSymbol member, IReadOnlyCollection<string> existingTestNames)
    {
        var prefix = MemberDisplay.GetTestName(member) + "_";
        return existingTestNames.Any(n => n.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// 計画したテストの件数と、要手動・生成不可の理由から状態を決める
    /// </summary>
    /// <param name="member">メンバー</param>
    /// <param name="testCount">計画したテストの件数</param>
    /// <param name="context">そのメンバーだけの生成の情報</param>
    /// <returns>テストの状態</returns>
    private static MemberCoverage Classify(IMethodSymbol member, int testCount, GenerationContext context)
    {
        if (0 < testCount) return new MemberCoverage(member, CoverageStatus.Generatable, []);

        var prefix = MemberDisplay.Of(member) + ": ";
        var reasons = context.NotGeneratableItems.Concat(context.ManualItems)
            .Select(r => r.StartsWith(prefix, StringComparison.Ordinal) ? r.Substring(prefix.Length) : r)
            .ToList();
        return new MemberCoverage(member, reasons.Count == 0 ? CoverageStatus.NothingToTest : CoverageStatus.NotGeneratable, reasons);
    }
}
