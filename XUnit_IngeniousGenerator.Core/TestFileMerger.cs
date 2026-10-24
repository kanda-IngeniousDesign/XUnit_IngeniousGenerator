using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace XUnit_IngeniousGenerator.Core;

/// <summary>
/// 生成したテストコードを既存のテストファイルにマージする
/// （既存のメンバーには触れず、名前がまだないメンバーと using だけを構文操作で追加する）
/// </summary>
public static class TestFileMerger
{
    // 生成コードはクラスのメンバーを空白4つで字下げしている
    private const string GeneratedMemberIndent = "    ";

    /// <summary>
    /// 既存のテストファイルに、生成したテストクラスのうち不足しているメンバーを追加する
    /// </summary>
    /// <param name="existingCode">既存のテストファイルの内容</param>
    /// <param name="generatedCode">新たに生成したテストファイルの内容</param>
    /// <returns>マージ後の内容（追加するものがない、または対応するテストクラスが見つからない場合はnull）</returns>
    public static string? Merge(string existingCode, string generatedCode)
    {
        var existingRoot = CSharpSyntaxTree.ParseText(existingCode).GetCompilationUnitRoot();
        var generatedRoot = CSharpSyntaxTree.ParseText(generatedCode).GetCompilationUnitRoot();

        var generatedClass = generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (generatedClass is null) return null;

        var existingClass = existingRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == generatedClass.Identifier.ValueText);
        if (existingClass is null) return null;

        // 同じ名前のメンバーは人間が編集している可能性があるため、追加も上書きもしない
        var existingNames = new HashSet<string>(existingClass.Members.Select(GetMemberName).OfType<string>(), StringComparer.Ordinal);
        var newMembers = generatedClass.Members.Where(m => GetMemberName(m) is { } name && !existingNames.Contains(name)).ToList();
        if (newMembers.Count == 0) return null;

        var endOfLine = DetectEndOfLine(existingRoot);
        var memberIndent = DetectMemberIndent(existingClass);
        var adjusted = newMembers.Select(m => AdjustTrivia(m, endOfLine, memberIndent));

        var mergedClass = existingClass.WithMembers(existingClass.Members.AddRange(adjusted));
        var mergedRoot = existingRoot.ReplaceNode(existingClass, mergedClass);
        return AddMissingUsings(mergedRoot, generatedRoot, endOfLine).ToFullString();
    }

    /// <summary>
    /// 重複判定に使うメンバー名を返す（メソッド・プロパティ・フィールド）
    /// </summary>
    /// <param name="member">メンバー</param>
    /// <returns>メンバー名（対象外のメンバーはnull）</returns>
    private static string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
            _ => null,
        };
    }

    /// <summary>
    /// 生成したメンバーの改行コードと字下げを既存ファイルに合わせ、前に空行を1つ入れる
    /// </summary>
    /// <param name="member">生成したメンバー</param>
    /// <param name="endOfLine">既存ファイルの改行コード</param>
    /// <param name="memberIndent">既存ファイルのメンバーの字下げ</param>
    /// <returns>調整したメンバー</returns>
    private static MemberDeclarationSyntax AdjustTrivia(MemberDeclarationSyntax member, string endOfLine, string memberIndent)
    {
        var rewritten = (MemberDeclarationSyntax)new TriviaAdjuster(endOfLine, memberIndent).Visit(member);
        var leading = rewritten.GetLeadingTrivia().SkipWhile(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

        // 生成コードではメンバーの後ろに空行が付いていることがあるため、改行1つにそろえる
        return rewritten
            .WithLeadingTrivia(new[] { EndOfLine(endOfLine) }.Concat(leading))
            .WithTrailingTrivia(EndOfLine(endOfLine));
    }

    /// <summary>
    /// 生成コードの using のうち、既存ファイルにないものを末尾に追加する
    /// </summary>
    /// <param name="root">既存ファイル（メンバー追加後）</param>
    /// <param name="generatedRoot">生成コード</param>
    /// <param name="endOfLine">既存ファイルの改行コード</param>
    /// <returns>using を追加したファイル</returns>
    private static CompilationUnitSyntax AddMissingUsings(CompilationUnitSyntax root, CompilationUnitSyntax generatedRoot, string endOfLine)
    {
        var existing = new HashSet<string>(
            root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name?.ToString() ?? string.Empty),
            StringComparer.Ordinal);
        var missing = generatedRoot.Usings
            .Where(u => u.Name is not null && !existing.Contains(u.Name.ToString()))
            .Select(u => UsingDirective(ParseName(u.Name!.ToString()))
                .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
                .WithTrailingTrivia(EndOfLine(endOfLine)))
            .ToArray();
        if (missing.Length == 0) return root;

        // 既存の using がない（先頭がコメントなどの）ファイルでは、先頭の空白・コメントを保ったまま追加する
        return root.Usings.Count == 0
            ? root.WithUsings(List(missing)).WithLeadingTrivia(root.GetLeadingTrivia())
            : root.AddUsings(missing);
    }

    /// <summary>
    /// ファイルで使われている改行コードを返す（見つからなければ CRLF）
    /// </summary>
    /// <param name="root">ファイル</param>
    /// <returns>改行コード</returns>
    private static string DetectEndOfLine(SyntaxNode root)
    {
        var first = root.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        return first.IsKind(SyntaxKind.EndOfLineTrivia) ? first.ToString() : "\r\n";
    }

    /// <summary>
    /// 既存のテストクラスのメンバーの字下げを返す（メンバーがなければクラスの字下げ＋空白4つ）
    /// </summary>
    /// <param name="testClass">既存のテストクラス</param>
    /// <returns>メンバーの字下げ</returns>
    private static string DetectMemberIndent(ClassDeclarationSyntax testClass)
    {
        var member = testClass.Members.FirstOrDefault();
        if (member is not null) return GetLineIndent(member.GetFirstToken());

        return GetLineIndent(testClass.GetFirstToken()) + GeneratedMemberIndent;
    }

    /// <summary>
    /// トークンの直前（同じ行の行頭）の空白を返す
    /// </summary>
    /// <param name="token">トークン</param>
    /// <returns>行頭の空白</returns>
    private static string GetLineIndent(SyntaxToken token)
    {
        var whitespace = token.LeadingTrivia.LastOrDefault();
        return whitespace.IsKind(SyntaxKind.WhitespaceTrivia) ? whitespace.ToString() : string.Empty;
    }

    /// <summary>
    /// 改行コードを置き換え、行頭の字下げの基準（空白4つ）を既存ファイルのメンバーの字下げに置き換える
    /// </summary>
    private sealed class TriviaAdjuster : CSharpSyntaxRewriter
    {
        private readonly string _endOfLine;
        private readonly string _memberIndent;

        /// <summary>
        /// 置き換えの内容を設定する
        /// </summary>
        /// <param name="endOfLine">改行コード</param>
        /// <param name="memberIndent">メンバーの字下げ</param>
        public TriviaAdjuster(string endOfLine, string memberIndent)
            : base(visitIntoStructuredTrivia: false)
        {
            _endOfLine = endOfLine;
            _memberIndent = memberIndent;
        }

        /// <summary>
        /// トークンの前後の空白・改行を置き換える
        /// </summary>
        /// <param name="token">トークン</param>
        /// <returns>置き換えたトークン</returns>
        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            return token
                .WithLeadingTrivia(token.LeadingTrivia.Select(t => Replace(t, isLeading: true)))
                .WithTrailingTrivia(token.TrailingTrivia.Select(t => Replace(t, isLeading: false)));
        }

        /// <summary>
        /// 改行は既存ファイルの改行コードに、行頭の空白は既存ファイルの字下げに置き換える
        /// </summary>
        /// <param name="trivia">トリビア</param>
        /// <param name="isLeading">トークンの前のトリビアか（生成コードでは行頭の字下げ）</param>
        /// <returns>置き換えたトリビア</returns>
        private SyntaxTrivia Replace(SyntaxTrivia trivia, bool isLeading)
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia)) return EndOfLine(_endOfLine);
            if (!isLeading || !trivia.IsKind(SyntaxKind.WhitespaceTrivia)) return trivia;

            var text = trivia.ToString();
            return text.StartsWith(GeneratedMemberIndent, StringComparison.Ordinal)
                ? Whitespace(_memberIndent + text.Substring(GeneratedMemberIndent.Length))
                : trivia;
        }
    }
}
