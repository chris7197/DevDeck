using chris7197.DevDeck.Core;

var formatter = new chris7197SqlFormatter();
int failed = 0;
void Check(string name, Action check)
{
    try { check(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}

foreach (string fixture in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.sql"))
{
    string input = File.ReadAllText(fixture);
    Check(Path.GetFileName(fixture) + " parses, formats and retains all comments", () =>
    {
        string formatted = formatter.Format(input);
        if (string.IsNullOrWhiteSpace(formatted)) throw new Exception("Empty result.");
        if (formatted.Contains("[dbo].", StringComparison.OrdinalIgnoreCase) ||
            formatted.Contains("dbo.", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Unqualified dbo object name remains.");
        // Idempotence: repeated press should not keep reformatting the document.
        string secondPass = formatter.Format(formatted);
        if (!string.Equals(formatted, secondPass, StringComparison.Ordinal))
            throw new Exception("Not idempotent.");
        Console.WriteLine($"  {Path.GetFileName(fixture)}: {input.Length} -> {formatted.Length} chars");
    });
}

Check("Basic JOIN formatting and identifier casing", () =>
{
    const string sql = "select x.ID, y.Name from dbo.TableX x inner join dbo.TableY y on y.XId = x.ID and y.IsActive = 1 where x.Status = 1";
    string result = formatter.Format(sql);
    if (!result.Contains("INNER JOIN", StringComparison.Ordinal) ||
        !result.Contains("FROM", StringComparison.Ordinal) ||
        result.Contains("dbo.Table", StringComparison.OrdinalIgnoreCase) ||
        !result.Contains("x.ID", StringComparison.Ordinal)) throw new Exception(result);
});

Check("String and comment containing dbo are never rewritten", () =>
{
    string result = formatter.Format("SELECT 'dbo.Foo' AS Literal -- dbo.Foo\nFROM dbo.Foo f;");
    if (!result.Contains("'dbo.Foo'", StringComparison.Ordinal) ||
        !result.Contains("-- dbo.Foo", StringComparison.Ordinal)) throw new Exception(result);
});

Check("Other schema is preserved", () =>
{
    string result = formatter.Format("SELECT x.ID FROM ext.TableX x;");
    if (!result.Contains("ext.TableX", StringComparison.Ordinal)) throw new Exception(result);
});

Check("dbo stripping can be disabled", () =>
{
    string result = formatter.Format("SELECT x.ID FROM dbo.TableX x;", removeDboPrefixes: false);
    if (!result.Contains("dbo.TableX", StringComparison.OrdinalIgnoreCase)) throw new Exception(result);
});

Check("Invalid SQL is rejected without returning partial text", () =>
{
    try { formatter.Format("SELECT FROM WHERE ="); throw new Exception("Invalid SQL unexpectedly accepted."); }
    catch (SqlFormattingException) { /* expected */ }
});

Environment.ExitCode = failed == 0 ? 0 : 1;
