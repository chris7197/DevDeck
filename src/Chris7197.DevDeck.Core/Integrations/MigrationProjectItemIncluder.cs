using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace chris7197.DevDeck.Core;

public enum MigrationIncludeMethod
{
    SdkImplicit,
    AlreadyIncluded,
    VisualStudio,
    ProjectFileUpdated
}

/// <summary>
/// Adds a generated .cs file to a traditional, explicitly-listed MSBuild project.
/// SDK projects are left untouched. Edits are prepared in a temporary file first.
/// </summary>
public sealed class MigrationProjectItemIncluder
{
    public static bool IsLegacyProject(string projectFile)
    {
        var doc = XDocument.Load(projectFile);
        XElement root = doc.Root ?? throw new InvalidDataException("Invalid project XML.");
        return IsLegacyProjectXml(root);
    }

    public MigrationIncludeMethod EnsureIncluded(string projectFile, string sourceFile)
    {
        projectFile = Path.GetFullPath(projectFile);
        sourceFile = Path.GetFullPath(sourceFile);
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("Generated file is missing.", sourceFile);

        string projectDirectory = Path.GetDirectoryName(projectFile)!;
        string relative = Path.GetRelativePath(projectDirectory, sourceFile).Replace('/', '\\');
        if (relative.StartsWith("..\\", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("The generated file must be inside the migration project directory.");

        // Parse the same snapshot that the atomic committer will compare against.
        byte[] originalBytes = File.ReadAllBytes(projectFile);
        using var originalStream = new MemoryStream(originalBytes, writable: false);
        var document = XDocument.Load(originalStream, LoadOptions.PreserveWhitespace);
        XElement root = document.Root ?? throw new InvalidDataException("Invalid project XML.");
        if (!IsLegacyProjectXml(root)) return MigrationIncludeMethod.SdkImplicit;

        XNamespace ns = root.Name.Namespace;
        foreach (XElement compile in root.Descendants(ns + "Compile"))
        {
            string? expression = (string?)compile.Attribute("Include");
            if (string.IsNullOrWhiteSpace(expression)) continue;
            foreach (string part in expression.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsIncluded(part, relative, projectDirectory, sourceFile))
                    return MigrationIncludeMethod.AlreadyIncluded;
            }
        }

        // Reuse an unconditional Compile ItemGroup. Conditional groups can make the
        // new migration disappear on different build configurations.
        XElement? group = root.Elements(ns + "ItemGroup")
            .FirstOrDefault(g => g.Attribute("Condition") is null &&
                g.Elements(ns + "Compile").Any(c => c.Attribute("Condition") is null));

        string original = File.ReadAllText(projectFile);
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (group is not null)
        {
            string itemIndent = FindItemIndent(group);
            XElement item = new(ns + "Compile", new XAttribute("Include", relative));
            if (group.LastNode is XText tail && string.IsNullOrWhiteSpace(tail.Value))
                tail.AddBeforeSelf(new XText(newline + itemIndent), item);
            else
                group.Add(new XText(newline + itemIndent), item, new XText(newline + "  "));
        }
        else
        {
            XElement newGroup = new(ns + "ItemGroup",
                new XText(newline + "    "),
                new XElement(ns + "Compile", new XAttribute("Include", relative)),
                new XText(newline + "  "));
            // Put the new ItemGroup before the build targets Import, when present.
            XElement? anchor = root.Elements(ns + "Import").LastOrDefault();
            if (anchor is not null)
                anchor.AddBeforeSelf(newGroup, new XText(newline + "  "));
            else if (root.LastNode is XText ending && string.IsNullOrWhiteSpace(ending.Value))
                ending.AddBeforeSelf(newGroup, new XText(newline + "  "));
            else
                root.Add(new XText(newline + "  "), newGroup, new XText(newline));
        }

        // An interrupted write must not truncate the original project file.
        string temp = Path.Combine(projectDirectory, ".devdeck-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Encoding encoding = DetectEncoding(projectFile, document.Declaration?.Encoding);
            using (var writer = XmlWriter.Create(temp, new XmlWriterSettings
            {
                Encoding = encoding,
                Indent = false,
                OmitXmlDeclaration = document.Declaration is null,
                NewLineHandling = NewLineHandling.None
            }))
                document.Save(writer);
            // Verify the replacement is valid XML and contains the new Compile item.
            var check = XDocument.Load(temp);
            if (!check.Descendants(ns + "Compile").Any(c => (string?)c.Attribute("Include") == relative))
                throw new InvalidDataException("The updated project does not contain the migration include.");
            AtomicProjectFileUpdater.Commit(projectFile, temp, originalBytes);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return MigrationIncludeMethod.ProjectFileUpdated;
    }

    private static bool IsLegacyProjectXml(XElement root) =>
        root.Attribute("Sdk") is null &&
        !root.Elements().Any(e => e.Name.LocalName == "Sdk" ||
            (e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null));

    private static string FindItemIndent(XElement group)
    {
        foreach (XNode node in group.Nodes())
        {
            if (node is not XText text || !string.IsNullOrWhiteSpace(text.Value)) continue;
            int i = text.Value.LastIndexOf('\n');
            if (i >= 0 && node.NextNode is XElement)
                return text.Value[(i + 1)..];
        }
        return "    ";
    }

    private static bool IsIncluded(string include, string relative, string directory, string fullFile)
    {
        string normalized = include.Replace('\\', '/');
        string target = relative.Replace('\\', '/');
        // A wildcard Compile include can already pick up the generated file.
        if (normalized.IndexOfAny(['*', '?']) >= 0 && !normalized.Contains("$(", StringComparison.Ordinal))
        {
            string regex = "^" + Regex.Escape(normalized)
                .Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]") + "$";
            return Regex.IsMatch(target, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (normalized.Contains("$(", StringComparison.Ordinal)) return false;
        try
        {
            string absolute = Path.GetFullPath(Path.Combine(directory,
                include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            return string.Equals(absolute, fullFile, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static Encoding DetectEncoding(string path, string? declared)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[4];
        int length = stream.Read(bytes);
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        if (!string.IsNullOrWhiteSpace(declared)) return Encoding.GetEncoding(declared);
        return new UTF8Encoding(false);
    }
}
