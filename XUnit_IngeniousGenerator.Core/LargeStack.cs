using System.Globalization;
using System.Runtime.ExceptionServices;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 解析・生成を、大きいスタックを持つ専用のスレッドで実行する。
/// IDE の解析のプロセス（Visual Studio の ServiceHub など）のスレッドはスタックが小さく、入れ子の深いコードを解析すると
/// スタックオーバーフローでプロセスごと落ちる（例外として捕まえられない）ため、その手前に余裕を持たせる
/// </summary>
internal static class LargeStack
{
    /// <summary>専用のスレッドのスタックの大きさ（16MB）</summary>
    private const int StackSize = 16 * 1024 * 1024;

    [ThreadStatic]
    private static bool _running;

    /// <summary>
    /// 処理を大きいスタックのスレッドで実行し、結果を返す（すでに専用のスレッドの中なら、そのまま実行する）
    /// </summary>
    /// <typeparam name="T">結果の型</typeparam>
    /// <param name="work">処理</param>
    /// <returns>処理の結果</returns>
    public static T Run<T>(Func<T> work)
    {
        if (_running) return work();

        // 理由文などを IDE の表示言語で書くため、呼び出し元の表示言語を引き継ぐ
        var uiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.CurrentCulture;
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(
            () =>
            {
                _running = true;
                CultureInfo.CurrentUICulture = uiCulture;
                CultureInfo.CurrentCulture = culture;
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackSize)
        {
            IsBackground = true,
            Name = "XUnit_IngeniousGenerator",
        };
        thread.Start();
        thread.Join();

        // 処理の中の例外（キャンセルなど）は、呼び出し元のスレッドで同じように投げ直す
        error?.Throw();
        return result;
    }
}
