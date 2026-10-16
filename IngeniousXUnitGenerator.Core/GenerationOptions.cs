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
    public GenerationOptions(bool useMoq, bool useVerify)
    {
        UseMoq = useMoq;
        UseVerify = useVerify;
    }

    /// <summary>Moq も Verify も使わない設定</summary>
    public static GenerationOptions None { get; } = new(false, false);

    /// <summary>インターフェースなどの依存を Moq でモックするか</summary>
    public bool UseMoq { get; }

    /// <summary>静的に計算できない戻り値を Verify で記録するか</summary>
    public bool UseVerify { get; }

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
}
