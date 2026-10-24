using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// テストプロジェクトで使えるパッケージに応じた生成の設定
/// </summary>
public sealed class GenerationOptions
{
    /// <summary>
    /// 生成の設定を作成する
    /// </summary>
    /// <param name="useMoq">インターフェースなどの依存を Moq でモックするか</param>
    /// <param name="useVerify">静的に計算できない戻り値を Verify で記録するか</param>
    /// <param name="onlyMember">このメソッド・コンストラクタのテストだけを生成する（null ならクラス全体）</param>
    public GenerationOptions(bool useMoq, bool useVerify, IMethodSymbol? onlyMember = null)
    {
        UseMoq = useMoq;
        UseVerify = useVerify;
        OnlyMember = onlyMember;
    }

    /// <summary>Moq も Verify も使わない設定</summary>
    public static GenerationOptions None { get; } = new(false, false);

    /// <summary>インターフェースなどの依存を Moq でモックするか</summary>
    public bool UseMoq { get; }

    /// <summary>静的に計算できない戻り値を Verify で記録するか</summary>
    public bool UseVerify { get; }

    /// <summary>このメソッド・コンストラクタのテストだけを生成する（null ならクラス全体）</summary>
    public IMethodSymbol? OnlyMember { get; }

    /// <summary>
    /// 指定のメソッド・コンストラクタのテストだけを生成する設定を作る
    /// </summary>
    /// <param name="member">メソッド・コンストラクタ（null ならクラス全体）</param>
    /// <returns>生成の設定</returns>
    public GenerationOptions WithOnlyMember(IMethodSymbol? member)
    {
        return new GenerationOptions(UseMoq, UseVerify, member);
    }

    /// <summary>
    /// テストプロジェクトが参照しているアセンブリ名から、使えるパッケージを判定する
    /// </summary>
    /// <param name="assemblyNames">参照しているアセンブリ名（拡張子なし）</param>
    /// <returns>生成の設定</returns>
    public static GenerationOptions FromReferencedAssemblies(IEnumerable<string> assemblyNames)
    {
        var names = new HashSet<string>(assemblyNames, StringComparer.OrdinalIgnoreCase);
        return new GenerationOptions(names.Contains("Moq"), names.Contains("Verify.Xunit"));
    }

    /// <summary>
    /// テストプロジェクトの csproj の内容（PackageReference）から、使えるパッケージを判定する
    /// （アナライザーはテストプロジェクトの参照を直接見られないため、csproj を読んで判定する）
    /// </summary>
    /// <param name="projectFileText">csproj の内容</param>
    /// <returns>生成の設定</returns>
    public static GenerationOptions FromProjectFile(string projectFileText)
    {
        return new GenerationOptions(HasPackageReference(projectFileText, "Moq"), HasPackageReference(projectFileText, "Verify.Xunit"));
    }

    /// <summary>
    /// csproj に、指定のパッケージの PackageReference があるかを判定する
    /// </summary>
    /// <param name="projectFileText">csproj の内容</param>
    /// <param name="packageId">パッケージID</param>
    /// <returns>あればtrue</returns>
    private static bool HasPackageReference(string projectFileText, string packageId)
    {
        var pattern = "<PackageReference\\s+[^>]*Include\\s*=\\s*\"" + Regex.Escape(packageId) + "\"";
        return Regex.IsMatch(projectFileText, pattern, RegexOptions.IgnoreCase);
    }
}
