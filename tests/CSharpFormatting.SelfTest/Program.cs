using System.Text;
using System.Text.Json;
using chris7197.DevDeck.Core;

var newLine = new CSharpFormattingSettings { EmbeddedStatementStyle = EmbeddedStatementStyle.NewLine };
var sameLine = new CSharpFormattingSettings { EmbeddedStatementStyle = EmbeddedStatementStyle.SameLine };
var signatures = new CSharpFormattingSettings { KeepMethodSignaturesOnSingleLine = true };

Check("Default", "if (ready) return;\n", "if (ready) return;\n", new());
Check("If and else", "if (ready)\n    Start();\nelse if (waiting)\n    Wait();\nelse\n    Stop();\n",
    "if (ready) Start();\nelse if (waiting) Wait();\nelse Stop();\n", sameLine);
Check("Expand if and else", "if (ready) Start();\nelse if (waiting) Wait();\nelse Stop();\n",
    "if (ready)\n    Start();\nelse if (waiting)\n    Wait();\nelse\n    Stop();\n", newLine);
Check("While", "while (hasMore)\n    Next();\n", "while (hasMore) Next();\n", sameLine);
Check("For", "for (int i = 0; i < 10; i++)\n    Next();\n", "for (int i = 0; i < 10; i++) Next();\n", sameLine);
Check("Foreach", "foreach (var item in items)\n    Use(item);\n", "foreach (var item in items) Use(item);\n", sameLine);
Check("Using and lock", "using (resource)\n    Use(resource);\nlock (gate)\n    Work();\n",
    "using (resource) Use(resource);\nlock (gate) Work();\n", sameLine);
Check("Fixed", "unsafe\n{\n    fixed (int* p = &value)\n        Use(p);\n}\n",
    "unsafe\n{\n    fixed (int* p = &value) Use(p);\n}\n", sameLine);
Check("Do while", "do\n    Next();\nwhile (hasMore);\n", "do Next();\nwhile (hasMore);\n", sameLine);
Check("Nested expansion", "if (ready) while (waiting) Next();\n", "if (ready)\n    while (waiting)\n        Next();\n", newLine);
Check("Nested compression", "if (ready)\n    while (waiting)\n        Next();\n", "if (ready)\n    while (waiting) Next();\n", sameLine);
Check("CRLF and tabs", "\tif (ready) return;\r\n", "\tif (ready)\r\n\t\treturn;\r\n",
    new() { EmbeddedStatementStyle = EmbeddedStatementStyle.NewLine, UseTabs = true });
Check("Braces unchanged", "if (ready)\n{\n    Next();\n}\n", "if (ready)\n{\n    Next();\n}\n", sameLine);
Check("Empty loop unchanged", "while (ready);\n", "while (ready);\n", newLine);
Check("Comment between header and body", "if (ready) // Why\n    Next();\n", "if (ready) // Why\n    Next();\n", sameLine);
Check("Trailing comment preserved", "if (ready)\n    Next(); // Why\n", "if (ready) Next(); // Why\n", sameLine);
Check("Wrapped body stays wrapped", "if (ready)\n    Next(\n        item\n    );\n", "if (ready)\n    Next(\n        item\n    );\n", sameLine);
Check("Wrapped condition stays wrapped", "if (\n    ready\n)\n    Next();\n", "if (\n    ready\n)\n    Next();\n", sameLine);
Check("Width limit", "if (ready)\n    LongMethodName(value);\n", "if (ready)\n    LongMethodName(value);\n",
    new() { EmbeddedStatementStyle = EmbeddedStatementStyle.SameLine, SameLineMaxLength = 20 });
Check("Blank line", "if (ready)\n\n    Next();\n", "if (ready)\n\n    Next();\n", sameLine);
Check("Preprocessor", "#if NEVER\nif (ready) Next();\n#else\nif (ready) Next();\n#endif\n",
    "#if NEVER\nif (ready) Next();\n#else\nif (ready)\n    Next();\n#endif\n", newLine);
Check("Ignore marker", "// csharpier-ignore\nif (ready) Next();\n", "// csharpier-ignore\nif (ready) Next();\n", newLine);
Check("Method signature and braces", "class C\n{\n    [Example]\n    public void M(\n        int x,\n        string y\n    )\n    {\n        Use(x, y);\n    }\n}\n",
    "class C\n{\n    [Example]\n    public void M(int x, string y)\n    {\n        Use(x, y);\n    }\n}\n", signatures);
Check("Generic constraint retained", "class C\n{\n    public T M<T>(\n        T x\n    )\n        where T : class\n    {\n        return x;\n    }\n}\n",
    "class C\n{\n    public T M<T>(T x)\n        where T : class\n    {\n        return x;\n    }\n}\n", signatures);
Check("Constructor and local function", "class C\n{\n    public C(\n        int x\n    )\n    {\n        void Local(\n            int y\n        )\n        {\n            Use(y);\n        }\n    }\n}\n",
    "class C\n{\n    public C(int x)\n    {\n        void Local(int y)\n        {\n            Use(y);\n        }\n    }\n}\n", signatures);
Check("Signature with comment", "class C\n{\n    void M(\n        int x, // why\n        int y\n    ) {}\n}\n",
    "class C\n{\n    void M(\n        int x, // why\n        int y\n    ) {}\n}\n", signatures);
Check("Signature containing multiline literal", "class C\n{\n    void M(\n        string x = @\"one\ntwo\"\n    ) {}\n}\n",
    "class C\n{\n    void M(\n        string x = @\"one\ntwo\"\n    ) {}\n}\n", signatures);
Check("Combined rules", "class C\n{\n    void M(\n        bool x\n    )\n    {\n        if (x) return;\n    }\n}\n",
    "class C\n{\n    void M(bool x)\n    {\n        if (x)\n            return;\n    }\n}\n",
    new() { EmbeddedStatementStyle = EmbeddedStatementStyle.NewLine, KeepMethodSignaturesOnSingleLine = true });

var deserialized = JsonSerializer.Deserialize<CSharpFormattingSettings>("""{"EmbeddedStatementStyle":"SameLine"}""");
if (deserialized?.EmbeddedStatementStyle != EmbeddedStatementStyle.SameLine || !deserialized.IsEnabled)
    throw new Exception("Settings did not deserialize as expected.");
Console.WriteLine("PASS: settings deserialization");

try
{
    CSharpPostFormatter.Format("if (", sameLine);
    throw new Exception("Expected invalid source to be rejected.");
}
catch (InvalidOperationException) { Console.WriteLine("PASS: invalid source rejected"); }

string tempDir = Path.Combine(Path.GetTempPath(), "devdeck-csharp-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
    {
        string path = Path.Combine(tempDir, "Example.cs");
        string input = "if (ready)\r\n    Next(\"é\");\r\n";
        string expected = "if (ready) Next(\"é\");\r\n";
        byte[] Bytes(string text) => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(path, Bytes(input));
        if (!await CSharpFormattedFile.ApplyAsync(path, sameLine) || !(await File.ReadAllBytesAsync(path)).SequenceEqual(Bytes(expected)))
            throw new Exception("Encoding, BOM or line endings changed incorrectly: " + encoding.WebName);
        if (await CSharpFormattedFile.ApplyAsync(path, sameLine)) throw new Exception("No-op should not rewrite the file.");
    }
    Console.WriteLine("PASS: file encoding, BOM, CRLF and no-op writes");
}
finally { Directory.Delete(tempDir, recursive: true); }
Console.WriteLine("All C# formatting self-tests passed.");

static void Check(string name, string input, string expected, CSharpFormattingSettings settings)
{
    string actual = CSharpPostFormatter.Format(input, settings);
    if (actual != expected)
        throw new Exception($"FAIL: {name}\nExpected:\n{expected}\nActual:\n{actual}");
    if (CSharpPostFormatter.Format(actual, settings) != actual)
        throw new Exception($"FAIL: {name} is not idempotent");
    Console.WriteLine("PASS: " + name);
}
