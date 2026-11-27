using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Applies house-style whitespace to the ScriptDom-generated SQL. AST nodes identify
/// the role of each token; literals, identifiers, operators and comments are never edited.
/// </summary>
internal sealed class SqlHouseStyleLayout : TSqlFragmentVisitor
{
    private const int TabSize = 4;
    private const int PreferredLineLength = 120;
    private readonly List<Part> _parts = [];
    private readonly Dictionary<int, int> _positions = [];
    private readonly Dictionary<int, int> _closingParentheses = [];
    private readonly HashSet<QueryExpression> _compactQueries = [];
    private readonly int _lastTokenIndex;

    private SqlHouseStyleLayout(string sql, TSqlFragment tree)
    {
        var parentheses = new Stack<int>();
        int offset = 0;
        for (int i = 0; i < tree.ScriptTokenStream.Count; i++)
        {
            TSqlParserToken token = tree.ScriptTokenStream[i];
            if (IsTrivia(token))
                continue;

            int index = _parts.Count;
            _positions.Add(i, index);
            _lastTokenIndex = i;
            _parts.Add(new Part(token, sql[offset..token.Offset]));
            offset = token.Offset + token.Text.Length;

            if (token.Text == "(")
                parentheses.Push(index);
            else if (token.Text == ")" && parentheses.Count > 0)
                _closingParentheses.Add(parentheses.Pop(), index);
        }
    }

    public static string Format(string sql, TSqlFragment tree)
    {
        var layout = new SqlHouseStyleLayout(sql, tree);
        tree.Accept(layout);
        return layout.Render();
    }

    // In addition to the existing AST equivalence check, ensure that this whitespace-only
    // pass cannot merge tokens, change literal contents or accidentally move an operator.
    public static void VerifyTokens(TSqlFragment before, TSqlFragment after)
    {
        var original = before.ScriptTokenStream.Where(t => !IsTrivia(t)).Select(t => (t.TokenType, t.Text));
        var updated = after.ScriptTokenStream.Where(t => !IsTrivia(t)).Select(t => (t.TokenType, t.Text));
        if (!original.SequenceEqual(updated))
            throw new SqlFormattingException("House-style formatting changed SQL tokens. Original text was kept.");
    }

    public override void Visit(IfStatement node)
    {
        int indent = Indent(Start(node));
        PlaceControlledStatement(node.ThenStatement, indent);

        if (node.ElseStatement is not null)
        {
            int elseKeyword = Find(End(node.ThenStatement) + 1, Start(node.ElseStatement) - 1, "ELSE");
            if (elseKeyword >= 0)
                BreakBefore(elseKeyword, indent);

            if (node.ElseStatement is IfStatement && elseKeyword == Start(node.ElseStatement) - 1)
                SpaceBefore(Start(node.ElseStatement));
            else
                PlaceControlledStatement(node.ElseStatement, indent);
        }
    }

    public override void Visit(WhileStatement node)
    {
        PlaceControlledStatement(node.Statement, Indent(Start(node)));
    }

    public override void Visit(BeginEndBlockStatement node)
    {
        int indent = Indent(Start(node));
        foreach (TSqlStatement statement in node.StatementList.Statements)
            Place(statement, indent + TabSize);

        int end = FindLast(Start(node), End(node), "END");
        if (end >= 0)
            BreakBefore(end, indent);
    }

    public override void Visit(StatementList node)
    {
        AddStatementSpacing(node.Statements);
    }

    public override void Visit(TSqlBatch node)
    {
        AddStatementSpacing(node.Statements);
    }

    public override void Visit(ScalarSubquery node)
    {
        int open = Find(Start(node), End(node), "(");
        if (!TryClose(open, End(node), out int close))
            return;

        AttachSubqueryOpening(open);
        if (IsSimpleSubquery(node.QueryExpression, open, close))
        {
            _compactQueries.Add(node.QueryExpression);
            for (int i = open + 1; i < close; i++)
            {
                if (HasNewline(_parts[i].Gap))
                    SpaceBefore(i);
            }
            TightBefore(open + 1);
            TightBefore(close);
            return;
        }

        PlaceParenthesizedBlock(open, close);
    }

    public override void Visit(QueryDerivedTable node)
    {
        int open = Find(Start(node), End(node), "(");
        if (TryClose(open, End(node), out int close))
            PlaceParenthesizedBlock(open, close);
    }

    public override void Visit(QueryParenthesisExpression node)
    {
        int open = Find(Start(node), End(node), "(");
        if (TryClose(open, End(node), out int close))
            PlaceParenthesizedBlock(open, close);
    }

    public override void Visit(CommonTableExpression node)
    {
        int asKeyword = Find(End(node.ExpressionName) + 1, Start(node.QueryExpression) - 1, "AS");
        if (asKeyword < 0)
            return;

        SpaceBefore(asKeyword);
        int open = Find(asKeyword + 1, Start(node.QueryExpression) - 1, "(");
        if (TryClose(open, End(node), out int close))
        {
            SpaceBefore(open);
            PlaceParenthesizedBlock(open, close);
        }
    }

    public override void Visit(QuerySpecification node)
    {
        if (_compactQueries.Contains(node))
            return;

        int indent = Indent(Start(node));
        foreach (SelectElement element in node.SelectElements)
            PlaceListItem(element, indent + TabSize);

        AlignClause(node.FromClause, indent);
        AlignClause(node.WhereClause, indent);
        AlignClause(node.GroupByClause, indent);
        AlignClause(node.HavingClause, indent);
        AlignClause(node.OrderByClause, indent);
    }

    public override void Visit(FromClause node)
    {
        // A compact SELECT has no line break between SELECT and FROM.
        int start = Start(node);
        if (!HasNewline(_parts[start].Gap))
            return;

        int indent = Indent(start) + TabSize;
        foreach (TableReference table in node.TableReferences)
            PlaceListItem(table, indent);
    }

    public override void Visit(WhereClause node)
    {
        Place(node.SearchCondition, Indent(Start(node)) + TabSize);
    }

    public override void Visit(HavingClause node)
    {
        Place(node.SearchCondition, Indent(Start(node)) + TabSize);
    }

    public override void Visit(GroupByClause node)
    {
        int indent = Indent(Start(node)) + TabSize;
        foreach (GroupingSpecification item in node.GroupingSpecifications)
            PlaceListItem(item, indent);
    }

    public override void Visit(OrderByClause node)
    {
        int indent = Indent(Start(node)) + TabSize;
        foreach (ExpressionWithSortOrder item in node.OrderByElements)
            PlaceListItem(item, indent);
    }

    public override void Visit(QualifiedJoin node)
    {
        int join = Find(End(node.FirstTableReference) + 1, Start(node.SecondTableReference) - 1, "JOIN");
        if (join < 0)
            return;

        int header = End(node.FirstTableReference) + 1;
        // Comments may precede the join keyword. Do not reposition the comment as a keyword.
        while (header < join && IsComment(_parts[header].Token))
            header++;

        int indent = Math.Max(0, Indent(Start(node.FirstTableReference)) - TabSize);
        BreakBefore(header, indent);
        Place(node.SecondTableReference, indent + TabSize);

        int on = Find(End(node.SecondTableReference) + 1, Start(node.SearchCondition) - 1, "ON");
        if (on >= 0)
            SpaceBefore(on);
        Place(node.SearchCondition, indent + 2 * TabSize);
    }

    public override void Visit(BooleanParenthesisExpression node)
    {
        if (node.Expression is not BooleanBinaryExpression)
            return;

        int open = Start(node);
        if (TryClose(open, End(node), out int close))
            PlaceParenthesizedBlock(open, close);
    }

    public override void Visit(BooleanBinaryExpression node)
    {
        int right = Start(node.SecondExpression);
        string keyword = node.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR";
        int op = Find(End(node.FirstExpression) + 1, right - 1, keyword);
        if (op < 0)
            return;

        // Only BooleanBinaryExpression operators are handled. BETWEEN's AND, literals
        // and commented-out SQL never enter this rule.
        if (IsComment(_parts[op - 1].Token) || IsComment(_parts[op + 1].Token))
            return;

        int indent = Indent(Start(node.FirstExpression));
        // If a predicate begins on the same line as IF/WHEN, use a continuation indent.
        if (!HasNewline(_parts[Start(node.FirstExpression)].Gap) && Start(node.FirstExpression) > 0)
            indent += TabSize;

        SpaceBefore(op);
        Place(node.SecondExpression, indent);
    }

    public override void Visit(BooleanComparisonExpression node)
    {
        int left = Start(node.FirstExpression);
        int right = Start(node.SecondExpression);
        int end = End(node.SecondExpression);
        if (node.SecondExpression is ScalarSubquery || right <= left || !IsSingleLine(left, end) ||
            Column(left) + FlatWidth(left, end) <= PreferredLineLength)
            return;

        if (FlatWidth(right, end) >= 20 && !IsComment(_parts[right - 1].Token))
            Place(node.SecondExpression, Indent(left) + TabSize);
    }

    public override void Visit(InPredicate node)
    {
        if (node.Values.Count == 0)
            return; // ScalarSubquery handles IN (SELECT ...).

        int first = Start(node.Values[0]);
        int last = End(node.Values[^1]);
        int open = Find(End(node.Expression) + 1, first - 1, "(");
        if (!TryClose(open, End(node), out int close))
            return;

        SpaceBefore(open);
        bool multiline = node.Values.Count > 5 || FlatWidth(first, last) > 64 ||
            Column(open) + FlatWidth(open, close) > PreferredLineLength || !IsSingleLine(first, last);
        if (!multiline)
            return;

        int indent = Indent(open);
        foreach (ScalarExpression value in node.Values)
            PlaceListItem(value, indent + TabSize);
        BreakBefore(close, indent);
    }

    public override void Visit(FunctionCall node)
    {
        int open = End(node.FunctionName) + 1;
        if (open <= End(node) && Is(open, "("))
            TightBefore(open);
    }

    public override void Visit(ConvertCall node) => TightFirstParenthesis(node);
    public override void Visit(CastCall node) => TightFirstParenthesis(node);
    public override void Visit(TryConvertCall node) => TightFirstParenthesis(node);
    public override void Visit(TryCastCall node) => TightFirstParenthesis(node);
    public override void Visit(SqlDataTypeReference node) => TightFirstParenthesis(node);
    public override void Visit(UserDataTypeReference node) => TightFirstParenthesis(node);

    public override void Visit(DeclareVariableStatement node)
    {
        if (node.Declarations.Count <= 1)
            return;

        int indent = Indent(Start(node)) + TabSize;
        foreach (DeclareVariableElement declaration in node.Declarations)
            PlaceListItem(declaration, indent);
    }

    public override void Visit(UpdateSpecification node)
    {
        if (node.SetClauses.Count == 0)
            return;

        int first = Start(node.SetClauses[0]);
        int set = FindLast(Start(node), first - 1, "SET");
        if (set < 0)
            return;

        int indent = Indent(Start(node));
        BreakBefore(set, indent);
        foreach (SetClause clause in node.SetClauses)
        {
            PlaceListItem(clause, indent + TabSize);
            int equals = Find(Start(clause), End(clause), "=");
            if (equals > Start(clause) && equals < End(clause) && !HasNewline(_parts[equals + 1].Gap))
            {
                SpaceBefore(equals);
                SpaceBefore(equals + 1);
            }
        }

        AlignClause(node.OutputIntoClause, indent);
        AlignClause(node.OutputClause, indent);
        AlignClause(node.FromClause, indent);
        AlignClause(node.WhereClause, indent);
    }

    public override void Visit(OutputClause node)
    {
        FormatOutput(node, node.SelectColumns);
    }

    public override void Visit(OutputIntoClause node)
    {
        FormatOutput(node, node.SelectColumns);
        if (node.SelectColumns.Count == 0)
            return;

        int into = Find(End(node.SelectColumns[^1]) + 1, End(node), "INTO");
        if (into < 0 || into == End(node))
            return;

        int indent = Indent(Start(node));
        BreakBefore(into, indent);
        BreakBefore(into + 1, indent + TabSize);

        int open = Find(End(node.IntoTable) + 1, End(node), "(");
        if (!TryClose(open, End(node), out int close))
            return;

        SpaceBefore(open);
        FormatCommaList(open, close, indent + TabSize);
    }

    public override void Visit(SearchedCaseExpression node)
    {
        FormatCase(node, node.WhenClauses, node.ElseExpression);
    }

    public override void Visit(SimpleCaseExpression node)
    {
        FormatCase(node, node.WhenClauses, node.ElseExpression);
    }

    private void FormatCase(TSqlFragment node, IEnumerable<TSqlFragment> clauses, ScalarExpression? otherwise)
    {
        int indent = Indent(Start(node));
        int previousEnd = Start(node);
        foreach (TSqlFragment clause in clauses)
        {
            Place(clause, indent + TabSize);
            previousEnd = End(clause);
        }

        if (otherwise is not null)
        {
            int keyword = Find(previousEnd + 1, Start(otherwise) - 1, "ELSE");
            if (keyword >= 0)
                BreakBefore(keyword, indent + TabSize);
        }

        int end = FindLast(Start(node), End(node), "END");
        if (end >= 0)
            BreakBefore(end, indent);
    }

    private void FormatOutput(TSqlFragment node, IList<SelectElement> columns)
    {
        int indent = Indent(Start(node));
        foreach (SelectElement column in columns)
            PlaceListItem(column, indent + TabSize);
    }

    private void FormatCommaList(int open, int close, int indent)
    {
        if (open + 1 >= close)
            return;

        BreakBefore(open + 1, indent + TabSize);
        for (int i = open + 1; i < close; i++)
        {
            if (Is(i, "(") && _closingParentheses.TryGetValue(i, out int nested))
                i = nested;
            else if (Is(i, ",") && i + 1 < close)
            {
                TightBefore(i);
                BreakBefore(i + 1, indent + TabSize);
            }
        }
        BreakBefore(close, indent);
    }

    private void AddStatementSpacing(IList<TSqlStatement> statements)
    {
        for (int i = 1; i < statements.Count; i++)
        {
            TSqlStatement current = statements[i];
            TSqlStatement previous = statements[i - 1];
            int start = Start(current);
            // Preserve comment attachment and never add a blank line between a comment
            // and the statement it documents.
            if (start <= 0 || IsComment(_parts[start - 1].Token) || !HasNewline(_parts[start].Gap))
                continue;

            bool separate = current is IfStatement or WhileStatement or TryCatchStatement ||
                previous is IfStatement or WhileStatement or TryCatchStatement ||
                current is BeginTransactionStatement or CommitTransactionStatement ||
                current is DeclareVariableStatement && previous is not DeclareVariableStatement ||
                previous is DeclareVariableStatement && current is not DeclareVariableStatement ||
                current is SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement;
            if (separate)
                BreakBefore(start, Indent(start), 2);
        }
    }

    private void AttachSubqueryOpening(int open)
    {
        if (open > 0 && (Is(open - 1, "EXISTS") || Is(open - 1, "IN") || Is(open - 1, "=")))
            SpaceBefore(open);
    }

    private bool IsSimpleSubquery(QueryExpression query, int open, int close)
    {
        if (query is not QuerySpecification specification || specification.SelectElements.Count != 1 ||
            specification.FromClause is null || specification.FromClause.TableReferences.Count != 1 ||
            specification.FromClause.TableReferences[0] is not (NamedTableReference or VariableTableReference) ||
            specification.WhereClause is not null || specification.GroupByClause is not null ||
            specification.HavingClause is not null || specification.OrderByClause is not null)
            return false;

        // Compact only plain SELECT <expression> FROM <table> queries. Avoid joins,
        // hints, nested calls, comments, DISTINCT/TOP, aliases with spaces and other clauses.
        for (int i = open + 1; i < close; i++)
        {
            if (IsComment(_parts[i].Token) || Is(i, "(") || Is(i, ",") || Is(i, "JOIN") ||
                Is(i, "TOP") || Is(i, "DISTINCT") || Is(i, "INTO") || HasNewline(_parts[i].Token.Text))
                return false;
        }

        return Indent(open) + FlatWidth(open, close) <= 80;
    }

    private void PlaceControlledStatement(TSqlStatement statement, int indent)
    {
        Place(statement, statement is BeginEndBlockStatement ? indent : indent + TabSize);
    }

    private void AlignClause(TSqlFragment? clause, int indent)
    {
        if (clause is not null)
            Place(clause, indent);
    }

    private void TightFirstParenthesis(TSqlFragment node)
    {
        int open = Find(Start(node) + 1, End(node), "(");
        if (open >= 0)
            TightBefore(open);
    }

    private void PlaceParenthesizedBlock(int open, int close)
    {
        if (open + 1 >= close)
            return;

        int indent = Indent(open);
        PlaceRange(open + 1, close - 1, indent + TabSize);
        BreakBefore(close, indent);
    }

    private void Place(TSqlFragment node, int indent) => PlaceRange(Start(node), End(node), indent);

    private void PlaceListItem(TSqlFragment node, int indent)
    {
        int start = Start(node);
        if (Is(start - 1, ","))
            TightBefore(start - 1);

        PlaceRange(start, End(node), indent);
    }

    // Shift existing lines with the containing node first. Child visitors then refine
    // their own clauses/scopes. No indentation depends on the length of IF EXISTS, etc.
    private void PlaceRange(int start, int end, int indent)
    {
        if (start < 0 || end < start || end >= _parts.Count)
            return;

        int oldAnchor = HasNewline(_parts[start].Gap) || start == 0 ? Indent(start) : RoundUp(Column(start));
        int delta = indent - oldAnchor;
        if (delta != 0)
        {
            for (int i = start + 1; i <= end; i++)
            {
                if (HasNewline(_parts[i].Gap))
                    BreakBefore(i, Math.Max(0, GapIndent(_parts[i].Gap) + delta), NewlineCount(_parts[i].Gap));
            }
        }

        BreakBefore(start, indent, Math.Max(1, NewlineCount(_parts[start].Gap)));
    }

    private void BreakBefore(int index, int indent, int lines = 1)
    {
        if (index < 0 || index >= _parts.Count)
            return;

        _parts[index].Gap = (index == 0 ? "" : new string('\n', Math.Max(1, lines))) + Tabs(indent);
    }

    private void SpaceBefore(int index)
    {
        if (CanJoin(index))
            _parts[index].Gap = " ";
    }

    private void TightBefore(int index)
    {
        if (CanJoin(index))
            _parts[index].Gap = "";
    }

    private bool CanJoin(int index) => index > 0 && index < _parts.Count &&
        !IsComment(_parts[index - 1].Token) && !IsComment(_parts[index].Token);

    private int Start(TSqlFragment node) => Position(node.FirstTokenIndex, 1);
    private int End(TSqlFragment node) => Position(node.LastTokenIndex, -1);

    private int Position(int tokenIndex, int direction)
    {
        if (tokenIndex < 0 || _positions.Count == 0)
            throw new SqlFormattingException("Unable to locate SQL syntax tokens. Original text was kept.");

        // ScriptDom fragment boundaries can include whitespace. Skip it to the nearest token.
        while (tokenIndex >= 0 && tokenIndex <= _lastTokenIndex)
        {
            if (_positions.TryGetValue(tokenIndex, out int position))
                return position;
            tokenIndex += direction;
        }
        throw new SqlFormattingException("Unable to locate SQL syntax tokens. Original text was kept.");
    }

    private bool Is(int index, string text) => index >= 0 && index < _parts.Count &&
        string.Equals(_parts[index].Token.Text, text, StringComparison.OrdinalIgnoreCase);

    private int Find(int first, int last, string text)
    {
        for (int i = Math.Max(0, first); i <= last && i < _parts.Count; i++)
        {
            if (Is(i, text))
                return i;
        }
        return -1;
    }

    private int FindLast(int first, int last, string text)
    {
        for (int i = Math.Min(last, _parts.Count - 1); i >= first && i >= 0; i--)
        {
            if (Is(i, text))
                return i;
        }
        return -1;
    }

    private bool TryClose(int open, int last, out int close) =>
        _closingParentheses.TryGetValue(open, out close) && close <= last;

    private int Indent(int index)
    {
        for (int i = index; i >= 0; i--)
        {
            if (i == 0 || HasNewline(_parts[i].Gap))
                return GapIndent(_parts[i].Gap);
            if (HasNewline(_parts[i - 1].Token.Text))
                return 0;
        }
        return 0;
    }

    private int Column(int index)
    {
        int first = index;
        while (first > 0 && !HasNewline(_parts[first].Gap) && !HasNewline(_parts[first - 1].Token.Text))
            first--;

        int column = GapIndent(_parts[first].Gap);
        for (int i = first; i < index; i++)
        {
            column = Advance(column, _parts[i].Token.Text);
            column = Advance(column, _parts[i + 1].Gap);
        }
        return column;
    }

    private int FlatWidth(int start, int end)
    {
        int width = 0;
        for (int i = start; i <= end; i++)
        {
            width += _parts[i].Token.Text.Length;
            if (i > start && _parts[i].Gap.Length > 0)
                width++;
        }
        return width;
    }

    private bool IsSingleLine(int start, int end)
    {
        for (int i = start; i <= end; i++)
        {
            if (HasNewline(_parts[i].Token.Text) || i > start && HasNewline(_parts[i].Gap))
                return false;
        }
        return true;
    }

    private static bool IsTrivia(TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile;

    private static bool IsComment(TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

    private static bool HasNewline(string text) => text.Contains('\n') || text.Contains('\r');
    private static int NewlineCount(string text) => text.Count(c => c == '\n') + text.Count(c => c == '\r') -
        (text.Length - text.Replace("\r\n", "\n", StringComparison.Ordinal).Length);
    private static int RoundUp(int value) => (value + TabSize - 1) / TabSize * TabSize;
    private static string Tabs(int indent) => new('\t', Math.Max(0, indent / TabSize));

    private static int GapIndent(string text)
    {
        int start = Math.Max(text.LastIndexOf('\n'), text.LastIndexOf('\r')) + 1;
        return Advance(0, text[start..]);
    }

    private static int Advance(int column, string text)
    {
        foreach (char c in text)
        {
            column = c switch
            {
                '\t' => RoundUp(column + 1),
                '\r' or '\n' => 0,
                _ => column + 1
            };
        }
        return column;
    }

    private string Render()
    {
        var result = new StringBuilder();
        foreach (Part part in _parts)
        {
            // Normalize only layout whitespace, not multiline strings or comments.
            result.Append(part.Gap.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal));
            result.Append(part.Token.Text);
        }
        return result.Append("\r\n").ToString();
    }

    private sealed class Part(TSqlParserToken token, string gap)
    {
        public TSqlParserToken Token { get; } = token;
        public string Gap { get; set; } = gap;
    }
}
