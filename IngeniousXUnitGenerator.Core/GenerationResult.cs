namespace IngeniousXUnitGenerator.Core;

/// <summary>
/// テストコード生成の結果（テストファイル1つ分）
/// </summary>
public sealed class GenerationResult
{
    /// <summary>
    /// 生成結果を作成する
    /// </summary>
    /// <param name="testClassName">生成したテストクラス名</param>
    /// <param name="code">生成したテストファイルのソースコード</param>
    public GenerationResult(string testClassName, string code)
    {
        TestClassName = testClassName;
        Code = code;
    }

    /// <summary>生成したテストクラス名（例: FooTests）</summary>
    public string TestClassName { get; }

    /// <summary>生成したテストファイルのソースコード</summary>
    public string Code { get; }
}
