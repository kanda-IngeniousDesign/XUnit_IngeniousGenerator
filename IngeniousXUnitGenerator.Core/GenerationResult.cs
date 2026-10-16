namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// テストコード生成の結果（テストファイル1つ分のコードとレポート）
/// </summary>
public sealed class GenerationResult
{
    /// <summary>
    /// 生成結果を作成する
    /// </summary>
    /// <param name="testClassName">生成したテストクラス名</param>
    /// <param name="code">生成したテストファイルのソースコード</param>
    /// <param name="generatedTestCount">生成したテストの件数</param>
    /// <param name="manualItems">自動生成できず、人間の対応が必要な項目</param>
    /// <param name="notGeneratableItems">外部環境などに依存し、単体テストでは担保しない項目</param>
    public GenerationResult(
        string testClassName, string code, int generatedTestCount, IReadOnlyList<string> manualItems, IReadOnlyList<string> notGeneratableItems)
    {
        TestClassName = testClassName;
        Code = code;
        GeneratedTestCount = generatedTestCount;
        ManualItems = manualItems;
        NotGeneratableItems = notGeneratableItems;
    }

    /// <summary>生成したテストクラス名（例: FooTests）</summary>
    public string TestClassName { get; }

    /// <summary>生成したテストファイルのソースコード</summary>
    public string Code { get; }

    /// <summary>生成したテストの件数</summary>
    public int GeneratedTestCount { get; }

    /// <summary>自動生成できず、人間の対応が必要な項目（テストコードにも「要手動」コメントで残す）</summary>
    public IReadOnlyList<string> ManualItems { get; }

    /// <summary>時刻・乱数・外部環境に依存し、UIオーケストレーター側で担保する項目（「生成不可」コメントで残す）</summary>
    public IReadOnlyList<string> NotGeneratableItems { get; }
}
