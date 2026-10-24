#nullable enable
using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Roslyn_IngeniousCommon;

// "// Ignore <ルールID>" による抑制の判定（アナライザー側で使う）。
// CSharp_IngeniousAnalyzer と XUnit_IngeniousGenerator で同じ規則にするため、ソースのパッケージとして共有する。
public static class IgnoreComment
{
    /// <summary>
    /// 指定ノードに "// Ignore &lt;DiagnosticId&gt;" 抑制コメントが付与されているかを判定する。
    /// ・ノード自身の直前行
    /// ・メソッド系宣言でブロック本体を持つ場合は、ブロック先頭（{ の直後）
    /// を許容する。
    /// </summary>
    /// <param name="node">判定するノード</param>
    /// <param name="diagnosticId">ルールID</param>
    /// <returns>Ignore コメントがあればtrue</returns>
    public static bool HasIgnoreComment(this SyntaxNode node, string diagnosticId)
    {
        if (HasIgnoreCommentInLeadingTrivia(node, diagnosticId)) return true;

        // ブロック本体を持つメソッド系宣言は、ブロック先頭（{ の直後）に書かれたコメントも許容する
        if (node is BaseMethodDeclarationSyntax { Body: { } body })
        {
            if (HasIgnoreCommentInLeadingTrivia(body, diagnosticId)) return true;

            var firstStmt = body.Statements.FirstOrDefault();
            if (firstStmt is not null && HasIgnoreCommentInLeadingTrivia(firstStmt, diagnosticId)) return true;
        }

        return false;
    }

    /// <summary>
    /// 指定ノードの先行トリビア（直前のコメント行）にのみ "// Ignore &lt;DiagnosticId&gt;" があるかを判定する。
    /// HasIgnoreComment と異なり、メソッド本体の先頭は見ない。
    /// </summary>
    /// <param name="node">判定するノード</param>
    /// <param name="diagnosticId">ルールID</param>
    /// <returns>Ignore コメントがあればtrue</returns>
    public static bool HasIgnoreCommentInLeadingTrivia(this SyntaxNode node, string diagnosticId)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (IsIgnoreCommentTrivia(trivia, diagnosticId)) return true;
        }
        return false;
    }

    /// <summary>
    /// トリビアが "// Ignore &lt;DiagnosticId&gt;" の1行コメントかを判定する（大文字・小文字は区別しない）
    /// </summary>
    /// <param name="trivia">判定するトリビア</param>
    /// <param name="diagnosticId">ルールID</param>
    /// <returns>Ignore コメントならtrue</returns>
    private static bool IsIgnoreCommentTrivia(SyntaxTrivia trivia, string diagnosticId)
    {
        if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
        {
            return false;
        }

        var text = trivia.ToString().Trim();
        return text.Equals($"// Ignore {diagnosticId}", StringComparison.OrdinalIgnoreCase);
    }
}