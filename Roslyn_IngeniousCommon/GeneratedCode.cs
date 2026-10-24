#nullable enable
using System;
using Microsoft.CodeAnalysis;

namespace Roslyn_IngeniousCommon;

// 自動生成ファイル（デザイナー・XAML など）の判定。人が書いたコードではないため、ルールの対象外にする
public static class GeneratedCode
{
    /// <summary>
    /// 自動生成ファイル（*.Designer.cs / *.g.cs / *.generated.cs）かを判定する
    /// </summary>
    /// <param name="tree">判定する構文木</param>
    /// <returns>自動生成ファイルならtrue</returns>
    public static bool IsGeneratedFile(SyntaxTree tree)
    {
        var filePath = tree.FilePath;
        return filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || // WPF/XAML自動生成用
            filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }
}
