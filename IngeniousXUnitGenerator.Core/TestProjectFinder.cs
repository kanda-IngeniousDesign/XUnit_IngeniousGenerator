using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// テスト対象のプロジェクトに対応するxUnitテストプロジェクトを探す
/// </summary>
public static class TestProjectFinder
{
    // xUnit v2 は xunit.core.dll、v3 は xunit.v3.core.dll
    private static readonly string[] XUnitAssemblyNames = ["xunit.core", "xunit.v3.core"];

    /// <summary>
    /// 対象プロジェクトを参照し、かつxUnitを参照しているプロジェクトを列挙する
    /// </summary>
    /// <param name="solution">ソリューション</param>
    /// <param name="targetProject">テスト対象のプロジェクト</param>
    /// <returns>テストプロジェクトの一覧（名前順）</returns>
    public static IReadOnlyList<Project> Find(Solution solution, Project targetProject)
    {
        // 補完のたびに呼ばれるため、コンパイルは作らずに参照情報だけで判定する
        return solution.Projects
            .Where(p => p.Id != targetProject.Id)
            .Where(p => p.ProjectReferences.Any(r => r.ProjectId == targetProject.Id))
            .Where(ReferencesXUnit)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// プロジェクトがxUnitのアセンブリを参照しているかを判定する
    /// </summary>
    /// <param name="project">判定するプロジェクト</param>
    /// <returns>参照していればtrue</returns>
    private static bool ReferencesXUnit(Project project)
    {
        return project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath ?? string.Empty))
            .Any(name => XUnitAssemblyNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }
}
