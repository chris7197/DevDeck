using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace chris7197.DevDeck.Core;

/// <summary>SQL Server house style based on vwBIOrderBookList and spUIUpdateProductionLine.</summary>
/// <remarks>
/// Parse/generate/parse, preserve every comment and check the AST's canonical rendering before
/// returning a candidate. No regex replacements on the original SQL and no partial output on error.
/// </remarks>
public sealed class chris7197SqlFormatter
{
    public const int MaxSqlCharacters = 2_000_000;

    public string Format(string sql, bool removeDboPrefixes = true)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new SqlFormattingException("There is no SQL to format.");
        if (sql.Length > MaxSqlCharacters)
            throw new SqlFormattingException("SQL exceeds the 2-million-character safety limit.");

        TSqlFragment original = Parse(sql);
        // Work from an AST. Only two-part dbo.ObjectName references are changed: schema-only
        // references in comments, strings and three/four-part object names are NOT touched.
        if (removeDboPrefixes) original.Accept(new RemoveDboSchemaVisitor());

        SqlScriptGeneratorOptions options = CreateOptions();
        string candidate = Generate(original, options);
        // Formatting may put AND at the beginning of predicate lines. Our convention uses
        // trailing AND. This pass is checked by re-parsing and structural comparison below.
        candidate = MoveLeadingAndToPreviousPredicate(candidate);
        candidate = SqlHouseStyleLayout.Apply(candidate);
        candidate = NormalizeNewlines(candidate);

        TSqlFragment result = Parse(candidate);
        string canonicalOriginal = Generate(original, CreateCanonicalOptions());
        string canonicalResult = Generate(result, CreateCanonicalOptions());
        if (!string.Equals(canonicalOriginal, canonicalResult, StringComparison.Ordinal))
            throw new SqlFormattingException("Formatting failed the SQL structural equivalence check. Original text was kept.");

        // SQL comments are important documentation. Never silently discard or change one.
        string[] originalComments = Comments(sql);
        string[] resultComments = Comments(candidate);
        if (!originalComments.SequenceEqual(resultComments, StringComparer.Ordinal))
            throw new SqlFormattingException("The formatter could not preserve every SQL comment. Original text was kept.");

        return candidate;
    }

    private static TSqlFragment Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0)
        {
            ParseError first = errors[0];
            throw new SqlFormattingException($"SQL parser error at line {first.Line}, column {first.Column}: {first.Message}");
        }
        return fragment;
    }

    private static SqlScriptGeneratorOptions CreateOptions() => new()
    {
        KeywordCasing = KeywordCasing.Uppercase,
        IdentifierCasing = IdentifierCasing.Preserve,
        IdentifierBracketing = IdentifierBracketing.Preserve,
        IndentationMode = IndentationMode.Tabs,
        IndentationSize = 4,
        PreserveComments = true,
        PersistTrailingGo = true,
        ClauseBodyAlignment = ClauseBodyAlignment.Indented,
        AlignClauseBodies = false,
        AsKeywordOnOwnLine = true,
        NewLineBeforeFromClause = true,
        NewLineBeforeJoinClause = true,
        NewLineAfterJoinKeyword = true,
        NewLineBeforeOnClause = false,
        NewLineBeforeWhereClause = true,
        NewLineBeforeGroupByClause = true,
        NewLineBeforeHavingClause = true,
        NewLineBeforeOrderByClause = true,
        MultilineSelectElementsList = true,
        MultilineWherePredicatesList = true,
        MultilineInsertTargetsList = true,
        MultilineInsertSourcesList = true,
        MultilineSetClauseItems = true,
        MultilineProcedureParametersList = true,
        NewLineBeforeOpenParenthesisInMultilineList = true,
        NewLineBeforeCloseParenthesisInMultilineList = true,
        MultilineNestedFunctionCalls = false,
        IncludeSemicolons = false,
        NumNewlinesAfterBatchStatement = 1,
        NumNewlinesAfterStatement = 1
    };

    private static SqlScriptGeneratorOptions CreateCanonicalOptions() => new()
    {
        KeywordCasing = KeywordCasing.Uppercase,
        IdentifierCasing = IdentifierCasing.Preserve,
        IdentifierBracketing = IdentifierBracketing.Preserve,
        PersistTrailingGo = true,
        PreserveComments = false,
        IncludeSemicolons = false,
        NumNewlinesAfterBatchStatement = 1,
        NumNewlinesAfterStatement = 1
    };

    private static string Generate(TSqlFragment fragment, SqlScriptGeneratorOptions options)
    {
        // SQL Server 2022 generator, matching the 2019/2022 databases used by this project.
        // Using the SQL 160 parser/generator also avoids newer SQL syntax being emitted to them.
        var generator = new Sql160ScriptGenerator(options);
        generator.GenerateScript(fragment, out string text);
        return text;
    }

    private static string NormalizeNewlines(string sql)
    {
        string normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').TrimEnd();
        return normalized.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
    }

    private static string MoveLeadingAndToPreviousPredicate(string sql)
    {
        string[] lines = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            Match match = Regex.Match(lines[i], @"^(?<indent>[\t ]+)AND[\t ]+(?<predicate>\S.*)$",
                RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            string previous = lines[i - 1].TrimEnd();
            if (previous.Length == 0 || previous.StartsWith("--", StringComparison.Ordinal) ||
                previous.Contains("--", StringComparison.Ordinal) || previous.EndsWith("*/", StringComparison.Ordinal) ||
                previous.EndsWith("AND", StringComparison.OrdinalIgnoreCase)) continue;
            lines[i - 1] = previous + " AND";
            lines[i] = match.Groups["indent"].Value + match.Groups["predicate"].Value;
        }
        return string.Join("\n", lines);
    }

    private static string[] Comments(string sql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader(sql);
        IList<TSqlParserToken> tokens = parser.GetTokenStream(reader, out IList<ParseError> errors);
        if (errors.Count > 0) throw new SqlFormattingException("Unable to tokenize SQL comments.");
        return tokens.Where(t => t.TokenType.ToString().Contains("Comment", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Text).ToArray();
    }

    private sealed class RemoveDboSchemaVisitor : TSqlFragmentVisitor
    {
        public override void Visit(SchemaObjectName node)
        {
            if (node.Identifiers.Count == 2 &&
                node.Identifiers[0].Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                node.Identifiers.RemoveAt(0);
        }
    }
}


/// <summary>
/// The ScriptDom generator owns SQL syntax and comments; this visitor only changes the
/// layout of *parsed* CASE/CTE/GROUP BY/JOIN constructs. Edits are applied from right to
/// left against generator output so ScriptDom source offsets remain valid. The caller
/// reparses and checks structural equivalence and comments before returning any output.
/// </summary>
internal sealed class SqlHouseStyleLayout : TSqlFragmentVisitor
{
    private readonly string _sql;
    private readonly List<TSqlParserToken> _tokens;
    private readonly Dictionary<(int Start, int End), string> _edits = new();

    private SqlHouseStyleLayout(string sql, TSqlFragment fragment)
    {
        _sql = sql;
        _tokens = fragment.ScriptTokenStream.ToList();
    }

    public static string Apply(string generatedSql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(generatedSql);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0)
            throw new SqlFormattingException("The generated SQL could not be parsed for house-style formatting.");

        var layout = new SqlHouseStyleLayout(generatedSql, fragment);
        fragment.Accept(layout);
        string result = generatedSql;
        foreach (var edit in layout._edits.OrderByDescending(e => e.Key.Start))
            result = result.Remove(edit.Key.Start, edit.Key.End - edit.Key.Start)
                .Insert(edit.Key.Start, edit.Value);
        return result;
    }

    public override void Visit(CommonTableExpression cte)
    {
        if (cte.QueryExpression is null) return;
        int nameEnd = End(cte.ExpressionName);
        int queryStart = cte.QueryExpression.StartOffset;
        TSqlParserToken? asToken = FindWord("AS", nameEnd, queryStart);
        TSqlParserToken? openParen = asToken is null ? null : FindToken("(", End(asToken), queryStart);
        TSqlParserToken? closeParen = FindToken(")", End(cte.QueryExpression), End(cte));
        if (asToken is null || openParen is null || closeParen is null) return;

        string indent = LineIndent(cte.ExpressionName.StartOffset);
        string prefix = LinePrefix(cte.ExpressionName.StartOffset).Trim();
        if (!prefix.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            // The generator indents subsequent CTE names one extra level.
            indent = Outdent(indent);
            TSqlParserToken? comma = PreviousToken(cte.ExpressionName.StartOffset);
            if (comma?.Text == ",") Gap(End(comma), cte.ExpressionName.StartOffset, "\n" + indent);
        }

        // Optional CTE column lists can appear between the name and AS.
        TSqlParserToken? beforeAs = PreviousToken(asToken.Offset);
        if (beforeAs is not null && End(beforeAs) >= nameEnd)
            Gap(End(beforeAs), asToken.Offset, " ");
        Gap(End(openParen), queryStart, "\n" + indent + "\t");
        Gap(End(cte.QueryExpression), closeParen.Offset, "\n" + indent);
    }

    public override void Visit(GroupByClause groupBy)
    {
        for (int i = 1; i < groupBy.GroupingSpecifications.Count; i++)
        {
            GroupingSpecification previous = groupBy.GroupingSpecifications[i - 1];
            GroupingSpecification current = groupBy.GroupingSpecifications[i];
            int start = End(previous);
            int end = current.StartOffset;
            if (start > end || end > _sql.Length) continue;
            string between = _sql.Substring(start, end - start);
            if (!System.Text.RegularExpressions.Regex.IsMatch(between, @"^\s*,\s*$")) continue;
            string indent = LineIndent(groupBy.GroupingSpecifications[0].StartOffset);
            GapWithComma(start, end, ",\n" + indent);
        }
    }

    public override void Visit(QualifiedJoin join)
    {
        if (join.SecondTableReference is null || join.SearchCondition is null) return;
        TableReference rhs = join.SecondTableReference;
        int leftEnd = End(join.FirstTableReference);
        int rightStart = rhs.StartOffset;
        TSqlParserToken? joinWord = FindWord("JOIN", leftEnd, rightStart);
        TSqlParserToken? onWord = FindWord("ON", End(rhs), join.SearchCondition.StartOffset);
        if (joinWord is null || onWord is null) return;

        TSqlParserToken? firstJoinWord = NextToken(leftEnd);
        if (firstJoinWord is null || firstJoinWord.Offset > joinWord.Offset) return;
        string joinIndent = Outdent(LineIndent(firstJoinWord.Offset));

        Gap(leftEnd, firstJoinWord.Offset, "\n" + joinIndent);
        Gap(End(joinWord), rightStart, "\n" + joinIndent + "\t");
        Gap(End(onWord), join.SearchCondition.StartOffset, "\n" + joinIndent + "\t\t");

        // The SQL generator writes AS on table aliases; table aliases in the house style
        // don't need it. Only touch a *named table* with an actual parsed alias.
        if (rhs is NamedTableReference named && named.Alias is not null)
        {
            TSqlParserToken? aliasAs = FindWord("AS", rhs.StartOffset, named.Alias.StartOffset);
            if (aliasAs is not null && _sql.Substring(End(aliasAs), named.Alias.StartOffset - End(aliasAs)).All(char.IsWhiteSpace))
                Replace(aliasAs.Offset, named.Alias.StartOffset, "");
        }

        IndentAndBranches(join.SearchCondition, joinIndent + "\t\t");
    }

    public override void Visit(SearchedCaseExpression expression) => Case(expression, expression.WhenClauses);
    public override void Visit(SimpleCaseExpression expression) => Case(expression, expression.WhenClauses);

    private void Case(CaseExpression expression, IEnumerable<WhenClause> clauses)
    {
        if (!clauses.Any()) return;
        int caseStart = expression.StartOffset;
        string indent = LineIndent(caseStart);
        string prefix = LinePrefix(caseStart).TrimEnd();
        if (prefix.EndsWith("(", StringComparison.Ordinal))
        {
            indent += "\t";
            TSqlParserToken? previous = PreviousToken(caseStart);
            if (previous?.Text == "(") Gap(End(previous), caseStart, "\n" + indent);
        }
        int previousEnd = caseStart + 4; // CASE token
        if (expression is SimpleCaseExpression simple && simple.InputExpression is not null)
            previousEnd = End(simple.InputExpression);
        foreach (WhenClause when in clauses)
        {
            Gap(previousEnd, when.StartOffset, "\n" + indent + "\t");
            TSqlFragment? condition = when switch
            {
                SearchedWhenClause searched => searched.WhenExpression,
                SimpleWhenClause simpleWhen => simpleWhen.WhenExpression,
                _ => null
            };
            if (condition is not null)
            {
                TSqlParserToken? thenToken = FindWord("THEN", End(condition), when.ThenExpression.StartOffset);
                if (thenToken is not null)
                {
                    string conditionText = _sql.Substring(condition.StartOffset, condition.FragmentLength);
                    string spacer = conditionText.Contains('\n') || conditionText.Length > 55
                        ? "\n" + indent + "\t\t"
                        : " ";
                    Gap(End(condition), thenToken.Offset, spacer);
                }
                if (when is SearchedWhenClause searched)
                    IndentAndBranches(searched.WhenExpression, indent + "\t\t");
            }
            previousEnd = End(when.ThenExpression);
        }

        if (expression.ElseExpression is not null)
        {
            TSqlParserToken? elseToken = FindWord("ELSE", previousEnd, expression.ElseExpression.StartOffset);
            if (elseToken is not null) Gap(previousEnd, elseToken.Offset, "\n" + indent + "\t");
            previousEnd = End(expression.ElseExpression);
        }
        TSqlParserToken? endToken = FindWord("END", previousEnd, End(expression));
        if (endToken is not null) Gap(previousEnd, endToken.Offset, "\n" + indent);
    }

    private void IndentAndBranches(BooleanExpression condition, string indentation)
    {
        // Only flatten AND at the top level. In particular never change OR precedence,
        // or the layout inside a nested query, expression or parenthesis.
        if (condition is not BooleanBinaryExpression binary ||
            binary.BinaryExpressionType != BooleanBinaryExpressionType.And) return;
        IndentAndBranches(binary.FirstExpression, indentation);
        TSqlParserToken? andToken = FindWord("AND", End(binary.FirstExpression), binary.SecondExpression.StartOffset);
        if (andToken is not null)
            Gap(End(andToken), binary.SecondExpression.StartOffset, "\n" + indentation);
        IndentAndBranches(binary.SecondExpression, indentation);
    }

    private static int End(TSqlFragment fragment) => fragment.StartOffset + fragment.FragmentLength;
    private static int End(TSqlParserToken token) => token.Offset + token.Text.Length;

    private string LineIndent(int offset)
    {
        int lineStart = _sql.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int i = lineStart;
        while (i < _sql.Length && (_sql[i] == '\t' || _sql[i] == ' ')) i++;
        return _sql.Substring(lineStart, i - lineStart);
    }

    private string LinePrefix(int offset)
    {
        int lineStart = _sql.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        return _sql.Substring(lineStart, offset - lineStart);
    }

    private static string Outdent(string indent) => indent.EndsWith("\t", StringComparison.Ordinal)
        ? indent[..^1] : indent.Length >= 4 && indent.EndsWith("    ", StringComparison.Ordinal)
        ? indent[..^4] : indent;

    private IEnumerable<TSqlParserToken> MeaningfulTokens(int start, int end) =>
        _tokens.Where(t => t.Offset >= start && t.Offset < end &&
                           !string.IsNullOrWhiteSpace(t.Text) &&
                           !t.TokenType.ToString().Contains("Comment", StringComparison.OrdinalIgnoreCase));

    private TSqlParserToken? FindWord(string text, int start, int end) =>
        MeaningfulTokens(start, end).FirstOrDefault(t => t.Text.Equals(text, StringComparison.OrdinalIgnoreCase));

    private TSqlParserToken? FindToken(string text, int start, int end) =>
        MeaningfulTokens(start, end).FirstOrDefault(t => t.Text == text);

    private TSqlParserToken? PreviousToken(int offset) =>
        MeaningfulTokens(0, offset).LastOrDefault(t => End(t) <= offset);

    private TSqlParserToken? NextToken(int offset) =>
        MeaningfulTokens(offset, _sql.Length).FirstOrDefault();

    private void Gap(int start, int end, string replacement)
    {
        if (start < 0 || end < start || end > _sql.Length) return;
        if (!_sql.AsSpan(start, end - start).ToString().All(char.IsWhiteSpace)) return;
        Replace(start, end, replacement);
    }

    private void GapWithComma(int start, int end, string replacement) => Replace(start, end, replacement);

    private void Replace(int start, int end, string replacement)
    {
        if (start < 0 || end < start || end > _sql.Length) return;
        if (_edits.TryGetValue((start, end), out string? old) && old != replacement) return;
        // An overlapping edit usually indicates an unusual nested construction. Leave its
        // original formatting alone rather than risking a broken statement.
        if (_edits.Keys.Any(k => start < k.End && k.Start < end && k != (start, end))) return;
        _edits[(start, end)] = replacement;
    }
}

public sealed class SqlFormattingException(string message) : Exception(message) { }
