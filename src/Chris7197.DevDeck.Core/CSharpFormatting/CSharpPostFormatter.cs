using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace chris7197.DevDeck.Core;

/// <summary>Applies narrowly scoped, syntax-aware whitespace edits to CSharpier output.</summary>
public static class CSharpPostFormatter
{
    private static readonly CSharpParseOptions _parseOptions = new(LanguageVersion.Preview);

    public static string Format(string code, CSharpFormattingSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (!settings.IsEnabled || string.IsNullOrWhiteSpace(code)) return code;

        // Be conservative around explicit formatter-ignore markers (including regions).
        if (code.Contains("csharpier-ignore", StringComparison.OrdinalIgnoreCase)) return code;

        SourceText source = SourceText.From(code);
        SyntaxNode root = Parse(source, cancellationToken);

        if (settings.KeepMethodSignaturesOnSingleLine)
        {
            var changes = new List<TextChange>();
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (node)
                {
                    case BaseMethodDeclarationSyntax method:
                        AddSignatureChanges(source, FirstAfterAttributes(method, method.AttributeLists),
                            method.ParameterList.CloseParenToken, changes);
                        break;
                    case LocalFunctionStatementSyntax local:
                        AddSignatureChanges(source, FirstAfterAttributes(local, local.AttributeLists),
                            local.ParameterList.CloseParenToken, changes);
                        break;
                }
            }
            (source, root) = ApplyAndValidate(source, root, changes, cancellationToken);
        }

        if (settings.EmbeddedStatementStyle != EmbeddedStatementStyle.Default)
        {
            var changes = new List<TextChange>();
            var movedIndents = new Dictionary<int, string>();
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetEmbeddedStatement(node, out SyntaxToken headerEnd, out StatementSyntax? body) && body is not null)
                    AddEmbeddedChanges(source, node, headerEnd, body, settings, movedIndents, changes);
            }
            (source, root) = ApplyAndValidate(source, root, changes, cancellationToken);
        }

        return source.ToString();
    }

    private static SyntaxNode Parse(SourceText source, CancellationToken cancellationToken)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, _parseOptions, cancellationToken: cancellationToken);
        Diagnostic? error = tree.GetDiagnostics(cancellationToken).FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null)
            throw new InvalidOperationException("DevDeck C# formatting skipped because Roslyn could not parse the file: " + error);
        return tree.GetRoot(cancellationToken);
    }

    private static (SourceText Source, SyntaxNode Root) ApplyAndValidate(SourceText source, SyntaxNode root,
        List<TextChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return (source, root);
        SourceText updated = source.WithChanges(changes.OrderBy(change => change.Span.Start));
        SyntaxNode updatedRoot = Parse(updated, cancellationToken);
        if (!SyntaxFactory.AreEquivalent(root, updatedRoot))
            throw new InvalidOperationException("DevDeck C# formatting changed the syntax tree. The post-formatting changes were not written.");
        return (updated, updatedRoot);
    }

    private static SyntaxToken FirstAfterAttributes(SyntaxNode node, SyntaxList<AttributeListSyntax> attributes)
    {
        return attributes.Count == 0 ? node.GetFirstToken() : attributes[^1].GetLastToken().GetNextToken();
    }

    private static void AddSignatureChanges(SourceText source, SyntaxToken first, SyntaxToken last, List<TextChange> changes)
    {
        if (first.RawKind == 0 || last.RawKind == 0 || first.SpanStart >= last.SpanStart ||
            LineNumber(source, first.SpanStart) == LineNumber(source, last.SpanStart)) return;

        // Stage the entire signature, so a comment, directive or multiline literal
        // leaves the whole declaration untouched rather than partially flattening it.
        var pending = new List<TextChange>();
        SyntaxToken previous = first;
        if (HasLineBreak(previous.Text)) return;
        while (previous != last)
        {
            SyntaxToken next = previous.GetNextToken();
            if (next.RawKind == 0 || next.SpanStart > last.SpanStart || HasLineBreak(next.Text)) return;
            TextSpan gap = TextSpan.FromBounds(previous.Span.End, next.SpanStart);
            string whitespace = source.ToString(gap);
            if (!IsLayoutWhitespace(whitespace)) return;
            if (HasLineBreak(whitespace))
            {
                bool tight = previous.IsKind(SyntaxKind.OpenParenToken) || previous.IsKind(SyntaxKind.OpenBracketToken) ||
                    previous.IsKind(SyntaxKind.DotToken) || previous.IsKind(SyntaxKind.ColonColonToken) ||
                    next.IsKind(SyntaxKind.CloseParenToken) || next.IsKind(SyntaxKind.CloseBracketToken) ||
                    next.IsKind(SyntaxKind.CommaToken) || next.IsKind(SyntaxKind.DotToken) ||
                    next.IsKind(SyntaxKind.ColonColonToken) ||
                    (previous.IsKind(SyntaxKind.LessThanToken) && IsTypeList(previous.Parent)) ||
                    (next.IsKind(SyntaxKind.GreaterThanToken) && IsTypeList(next.Parent));
                pending.Add(new TextChange(gap, tight ? "" : " "));
            }
            previous = next;
        }
        changes.AddRange(pending);
    }

    private static bool IsTypeList(SyntaxNode? node) => node is TypeArgumentListSyntax or TypeParameterListSyntax;

    private static bool TryGetEmbeddedStatement(SyntaxNode node, out SyntaxToken headerEnd, out StatementSyntax? body)
    {
        (headerEnd, body) = node switch
        {
            IfStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            ElseClauseSyntax clause => (clause.ElseKeyword, clause.Statement),
            WhileStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            ForStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            CommonForEachStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            UsingStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            LockStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            FixedStatementSyntax statement => (statement.CloseParenToken, statement.Statement),
            DoStatementSyntax statement => (statement.DoKeyword, statement.Statement),
            _ => (default(SyntaxToken), null)
        };
        return body is not null;
    }

    private static void AddEmbeddedChanges(SourceText source, SyntaxNode header, SyntaxToken headerEnd, StatementSyntax body,
        CSharpFormattingSettings settings, Dictionary<int, string> movedIndents, List<TextChange> changes)
    {
        // Never add/remove braces or move an empty loop statement. Preserve else-if chains.
        if (body is BlockSyntax or EmptyStatementSyntax || (header is ElseClauseSyntax && body is IfStatementSyntax)) return;
        SyntaxToken first = body.GetFirstToken();
        SyntaxToken headerStart = header.GetFirstToken();
        if (headerEnd.RawKind == 0 || first.RawKind == 0 || headerEnd.Span.End > first.SpanStart) return;
        TextSpan gap = TextSpan.FromBounds(headerEnd.Span.End, first.SpanStart);
        if (!IsLayoutWhitespace(source.ToString(gap))) return;

        int headerLine = LineNumber(source, headerEnd.SpanStart);
        int bodyLine = LineNumber(source, first.SpanStart);
        int bodyEndLine = LineNumber(source, body.Span.End - 1);
        if (settings.EmbeddedStatementStyle == EmbeddedStatementStyle.SameLine)
        {
            // Nested control flow, wrapped headers/bodies and blank-line separation
            // stay with CSharpier. Joining them would undermine readability.
            if (!IsSimpleStatement(body) || bodyLine != headerLine + 1 || bodyEndLine != bodyLine ||
                LineNumber(source, headerStart.SpanStart) != headerLine) return;
            TextLine headerTextLine = source.Lines[headerLine];
            TextLine bodyTextLine = source.Lines[bodyLine];
            string joined = source.ToString(TextSpan.FromBounds(headerTextLine.Start, headerEnd.Span.End)) + " " +
                source.ToString(TextSpan.FromBounds(first.SpanStart, bodyTextLine.End));
            if (ColumnWidth(joined, settings.IndentSize) > settings.SameLineMaxLength) return;
            changes.Add(new TextChange(gap, " "));
            return;
        }

        if (bodyLine != headerLine || bodyEndLine != bodyLine) return;
        // Do not re-indent unrelated tokens on the same line (compact blocks or a trailing else).
        // CSharpier normally lays those out over multiple lines already.
        SyntaxToken following = body.GetLastToken().GetNextToken();
        if (following.RawKind != 0 && !following.IsKind(SyntaxKind.EndOfFileToken) &&
            LineNumber(source, following.SpanStart) == bodyEndLine &&
            !(header is DoStatementSyntax && following.IsKind(SyntaxKind.WhileKeyword))) return;

        string indent = movedIndents.TryGetValue(headerStart.SpanStart, out string? moved) ? moved :
            GetIndent(source, headerStart.SpanStart);
        indent += settings.UseTabs ? "\t" : new string(' ', settings.IndentSize);
        string newLine = GetLineBreak(source, headerLine);
        changes.Add(new TextChange(gap, newLine + indent));
        movedIndents[first.SpanStart] = indent;
    }

    private static bool IsSimpleStatement(StatementSyntax body) => body is ExpressionStatementSyntax or ReturnStatementSyntax or
        ThrowStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax or GotoStatementSyntax or YieldStatementSyntax;

    private static bool IsLayoutWhitespace(string value) => value.All(c => c is ' ' or '\t' or '\r' or '\n');
    private static bool HasLineBreak(string value) => value.Contains('\r') || value.Contains('\n');
    private static int LineNumber(SourceText source, int position) => source.Lines.GetLineFromPosition(position).LineNumber;

    private static string GetIndent(SourceText source, int position)
    {
        TextLine line = source.Lines.GetLineFromPosition(position);
        int end = line.Start;
        while (end < position && source[end] is ' ' or '\t') end++;
        return source.ToString(TextSpan.FromBounds(line.Start, end));
    }

    private static string GetLineBreak(SourceText source, int lineNumber)
    {
        TextLine line = source.Lines[lineNumber];
        if (line.EndIncludingLineBreak > line.End)
            return source.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
        if (lineNumber > 0)
        {
            TextLine previous = source.Lines[lineNumber - 1];
            return source.ToString(TextSpan.FromBounds(previous.End, previous.EndIncludingLineBreak));
        }
        return Environment.NewLine;
    }

    private static int ColumnWidth(string line, int tabSize)
    {
        int width = 0;
        foreach (char c in line) width += c == '\t' ? tabSize - width % tabSize : 1;
        return width;
    }
}
