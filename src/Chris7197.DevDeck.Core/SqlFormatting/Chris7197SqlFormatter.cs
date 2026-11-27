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
        string generated = Generate(original, options);
        TSqlFragment generatedTree = Parse(generated);
        string candidate = SqlHouseStyleLayout.Format(generated, generatedTree);

        TSqlFragment result = Parse(candidate);
        SqlHouseStyleLayout.VerifyTokens(generatedTree, result);
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

public sealed class SqlFormattingException(string message) : Exception(message) { }
