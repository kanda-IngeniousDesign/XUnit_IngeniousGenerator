namespace IngeniousXUnitGenerator.Cli;

/// <summary>
/// dotnet tool のエントリーポイント
/// </summary>
internal static class Program
{
    private const string Usage = "使い方: dotnet ingenious-xunit gen --project <csproj> --class <クラス名> [--method <メソッド名>]";

    /// <summary>
    /// コマンドライン引数を解釈して生成処理を呼び出す
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>終了コード</returns>
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "gen")
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        // TODO: MSBuildWorkspaceでプロジェクトを読み込み、Coreの生成処理を呼ぶ（最初の一歩の3で実装）
        Console.Error.WriteLine("gen は未実装です。");
        return 1;
    }
}
