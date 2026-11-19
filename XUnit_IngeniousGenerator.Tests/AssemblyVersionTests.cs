namespace XUnit_IngeniousGenerator.Tests;

/// <summary>
/// 同梱する Core.dll のバージョンが、アナライザー（パッケージ）と同じであることを確かめる。
/// Core.dll のバージョンが変わらないと、VS は以前に読み込んだ古い Core.dll を使い続け、新しいアナライザーと食い違う
/// </summary>
public class AssemblyVersionTests
{
    [Fact]
    public void CoreAssembly_HasSameVersionAsAnalyzer()
    {
        var analyzer = typeof(MissingTest).Assembly.GetName().Version;
        var core = typeof(Core.TestCoverageInspector).Assembly.GetName().Version;

        Assert.Equal(analyzer, core);
        Assert.NotEqual(new Version(1, 0, 0, 0), core);
    }
}
