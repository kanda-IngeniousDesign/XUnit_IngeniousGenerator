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
    public GenerationResult(string testClassName, string code, int generatedTestCount, IReadOnlyList<string> manualItems)
    {
        TestClassName = testClassName;
        Code = code;
        GeneratedTestCount = generatedTestCount;
        ManualItems = manualItems;
    }

    /// <summary>生成したテストクラス名（例: FooTests）</summary>
    public string TestClassName { get; }

    /// <summary>生成したテストファイルのソースコード</summary>
    public string Code { get; }

    /// <summary>生成したテストの件数</summary>
    public int GeneratedTestCount { get; }

    /// <summary>自動生成できず、人間の対応が必要な項目（テストコードにもコメントで残す）</summary>
    public IReadOnlyList<string> ManualItems { get; }
}
