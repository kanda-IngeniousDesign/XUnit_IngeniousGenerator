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
        var members = TestTargets.Collect(targetType);
        var baseNames = members.Select(TestTargets.GetBaseName).ToList();
        SutPlan? sut = null;
        var results = new List<MemberCoverage>(members.Count);
        foreach (var member in members)
        {
            var baseName = TestTargets.GetBaseName(member);
            var testNames = existingTestNames.Where(n => BelongsTo(n, baseName, baseNames)).ToList();
            var notTestedName = baseName + TestTargets.NotTestedSuffix;
            if (testNames.Any(n => n != notTestedName))
            {
                results.Add(new MemberCoverage(member, CoverageStatus.Covered, []));
                continue;
            }

            sut ??= SutPlan.Create(targetType, new GenerationContext(compilation, options));
            var plan = MemberPlan.Create(member, targetType, sut, compilation, options);
            results.Add(Classify(member, plan, listed: testNames.Contains(notTestedName)));
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
    /// テストメソッドが、その関数のテスト（命名規約 Method_Condition_Expected で「テスト名の元になる名前_」で始まるもの）かを判定する。
    /// より長い名前の関数（Limit に対する Limit_Get、Add に対する Add_Int など）のテストは含めない
    /// </summary>
    /// <param name="testName">テストメソッド名</param>
    /// <param name="baseName">関数のテスト名の元になる名前</param>
    /// <param name="allBaseNames">クラス内のすべての関数のテスト名の元になる名前</param>
    /// <returns>その関数のテストならtrue</returns>
    private static bool BelongsTo(string testName, string baseName, IReadOnlyList<string> allBaseNames)
    {
        var prefix = baseName + "_";
        if (!testName.StartsWith(prefix, StringComparison.Ordinal)) return false;

        return !allBaseNames.Any(other => baseName.Length < other.Length
            && other.StartsWith(prefix, StringComparison.Ordinal)
            && testName.StartsWith(other + "_", StringComparison.Ordinal));
    }

    /// <summary>
    /// 計画したテストと理由から状態を決める（生成できるテストがあれば、スキップのテストだけが載っていても TEST001 の対象に戻す）
    /// </summary>
    /// <param name="member">関数</param>
    /// <param name="plan">関数の計画</param>
    /// <param name="listed">スキップのテスト（テストしていない一覧）に載っているか</param>
    /// <returns>テストの状態</returns>
    private static MemberCoverage Classify(IMethodSymbol member, MemberPlan plan, bool listed)
    {
        if (0 < plan.Tests.Count) return new MemberCoverage(member, CoverageStatus.Generatable, plan.Reasons);
        if (plan.Reasons.Count == 0) return new MemberCoverage(member, CoverageStatus.NothingToTest, []);
        return new MemberCoverage(member, listed ? CoverageStatus.Covered : CoverageStatus.NotGeneratable, plan.Reasons);
    }
}
