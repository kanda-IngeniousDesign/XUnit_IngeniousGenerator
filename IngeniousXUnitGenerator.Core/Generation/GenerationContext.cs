using Microsoft.CodeAnalysis;

namespace IngeniousXUnitGenerator.Core.Generation;

/// <summary>
/// 1つのテストクラスを生成する間に共有する情報（コンパイル・設定・レポート）
/// </summary>
internal sealed class GenerationContext
{
    /// <summary>
    /// 生成の情報を作成する
    /// </summary>
    /// <param name="compilation">テスト対象を含むコンパイル</param>
    /// <param name="options">生成の設定</param>
    public GenerationContext(Compilation compilation, GenerationOptions options)
    {
        Compilation = compilation;
        Options = options;
    }

    /// <summary>テスト対象を含むコンパイル</summary>
    public Compilation Compilation { get; }

    /// <summary>生成の設定</summary>
    public GenerationOptions Options { get; }

    /// <summary>要手動（人間がテストを書けば単体テストで担保できるもの）</summary>
    public List<string> ManualItems { get; } = [];

    /// <summary>生成不可（時刻・乱数・外部環境に依存し、単体テストでは担保しないもの）</summary>
    public List<string> NotGeneratableItems { get; } = [];
}
