using System.Resources;

namespace XUnit_IngeniousGenerator;

// 警告のタイトル・メッセージ（Resources.resx と、日本語の Resources.ja.resx）
internal static class Resources
{
    /// <summary>リソースの読み込み</summary>
    public static ResourceManager ResourceManager { get; } = new("XUnit_IngeniousGenerator.Resources", typeof(Resources).Assembly);
}
