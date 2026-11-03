using System.Diagnostics;
using System.Text;

namespace chris7197.DevDeck.Core;

public sealed record MigrationPreview(string ProjectFile, string MigrationFile, string MigrationNamespace, long Version, int OpenAtLine = Consts.OpenAtLineDefault, int OpenAtOffset = Consts.OpenAtOffsetDefault);
public sealed record MigrationCreation(MigrationPreview Preview, MigrationIncludeMethod IncludeMethod);

/// <summary>Generates your existing FluentMigrator naming convention without running migrations.</summary>
public sealed class NewMigrationService(MigrationProjectResolver resolver, DeckLog log,
    VisualStudioSourceNavigator navigator, MigrationProjectItemIncluder includer,
    VisualStudioProjectItemAdder projectItemAdder)
{
    public MigrationPreview Preview(string solutionPath)
    {
        MigrationProject project = resolver.Resolve(solutionPath);
        DateTime moment = DateTime.Now;
        for (int attempt = 0; attempt < 1440; attempt++, moment = moment.AddMinutes(1))
        {
            string[] segments = new[] { moment.ToString("yyyy"), moment.ToString("MM"),
                moment.ToString("dd"), moment.ToString("HH"), moment.ToString("mm") };
            string file = Path.Combine(new[] { project.MigratorDirectory, string.Join(".", segments) }.Append("Migrator.cs").ToArray());
            if (File.Exists(file)) continue;
            string ns = project.RootNamespace + "._" + string.Join("._", segments);
            return new MigrationPreview(project.ProjectFile, file, ns,
                long.Parse(moment.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)), project.OpenAtLine, project.OpenAtOffset);
        }
        throw new IOException("Could not find an unused minute for a migration.");
    }

    public async Task<MigrationCreation> CreateAsync(string solutionPath, bool openEditor = true)
    {
        MigrationPreview preview = Preview(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(preview.MigrationFile)!);
        string source = $@"using chris7197.DBMigrator.Micross.DBMigratorTemplate;
using FluentMigrator;
using System;

namespace {preview.MigrationNamespace}
{{
    [Migration({preview.Version})]
    public class Migrator : Migration
    {{
        public const string TableName = ""TableName"";
        public const string ColumnName = ""ColumnName"";

        public string VERSION
        {{
            get {{ return this.GetDBVersionFromNamespace(); }}
        }}

        public override void Up()
        {{
            Console.WriteLine("""");
            Console.WriteLine(""### UPDATING TO VERSION "" + VERSION + "" ###"");



            Console.WriteLine(""     - Completed changes"");
        }}

        public override void Down()
        {{
            Console.WriteLine("""");
            Console.WriteLine(""### ROLLING BACK VERSION "" + VERSION + "" ###"");


            Console.WriteLine(""     - Completed Rolling back changes"");
        }}
    }}
}}" + Environment.NewLine;

        // CreateNew ensures the action NEVER overwrites an existing migration.
        await using (var stream = new FileStream(preview.MigrationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(source);

        MigrationIncludeMethod inclusion;
        bool addedViaVisualStudio = false;
        try
        {
            if (!MigrationProjectItemIncluder.IsLegacyProject(preview.ProjectFile))
            {
                inclusion = MigrationIncludeMethod.SdkImplicit;
            }
            else
            {
                // The controller normally has an open VS instance. Use DTE first,
                // preventing an external project change/reload prompt. The CLI
                // without an open editor updates .csproj on disk instead.
                if (openEditor)
                {
                    try
                    {
                        addedViaVisualStudio = await projectItemAdder.TryAddAsync(
                            solutionPath, preview.ProjectFile, preview.MigrationFile, log);
                    }
                    catch (Exception e) when (e is VisualStudioAutomationTimeoutException or
                                                  VisualStudioAutomationUnavailableException or
                                                  VisualStudioProjectChangeUncertainException)
                    {
                        // The STA could still be finishing an earlier operation. It is NOT
                        // safe to fall back to an on-disk .csproj edit or undo the new file.
                        log.Write("Migration project inclusion uncertain; keeping generated file: " + e);
                        throw new MigrationInclusionUncertainException(preview.MigrationFile, e);
                    }
                    catch (Exception e)
                    {
                        log.Write("Visual Studio project-item automation unavailable: " + e.ToString());
                    }
                }
                // Re-read .csproj even after DTE reports success: this guarantees
                // the file was persisted as a Compile item, not just added in memory.
                inclusion = includer.EnsureIncluded(preview.ProjectFile, preview.MigrationFile);
                if (addedViaVisualStudio && inclusion == MigrationIncludeMethod.AlreadyIncluded)
                    inclusion = MigrationIncludeMethod.VisualStudio;
            }
        }
        catch (MigrationInclusionUncertainException)
        {
            // Deliberately preserve the file for manual verification; COM might finish later.
            throw;
        }
        catch (ProjectFileUpdateUncertainException e)
        {
            // An atomic replacement might have succeeded. Never delete the referenced .cs file.
            throw new MigrationInclusionUncertainException(preview.MigrationFile, e);
        }
        catch (Exception e) when (addedViaVisualStudio)
        {
            // DTE may have already persisted its project changes even if disk verification failed.
            throw new MigrationInclusionUncertainException(preview.MigrationFile, e);
        }
        catch
        {
            // A generated migration that the project cannot compile is misleading.
            // If inclusion fails, remove this new file and any new empty date folders.
            File.Delete(preview.MigrationFile);
            RemoveEmptyParents(Path.GetDirectoryName(preview.MigrationFile)!,
                Path.GetDirectoryName(preview.ProjectFile)!);
            throw;
        }

        log.Write($"Created migration {preview.Version}: {preview.MigrationFile} ({inclusion})");
        if (openEditor)
        {
            try { await navigator.OpenAtLineAndOffsetAsync(solutionPath, preview.MigrationFile, preview.OpenAtLine, preview.OpenAtOffset); }
            catch (Exception e)
            {
                log.Write("Could not open generated migration in Visual Studio: " + e.Message);
                try { Process.Start(new ProcessStartInfo(preview.MigrationFile) { UseShellExecute = true }); }
                catch (Exception openError) { log.Write("No associated code editor: " + openError.Message); }
            }
        }
        return new MigrationCreation(preview, inclusion);
    }

    private static void RemoveEmptyParents(string directory, string projectDirectory)
    {
        while (!string.Equals(directory, projectDirectory, StringComparison.OrdinalIgnoreCase) &&
            directory.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) break;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }
}

/// <summary>A project may still be changing inside Visual Studio. Manual inspection required.</summary>
public sealed class MigrationInclusionUncertainException(string file, Exception inner)
    : InvalidOperationException("Could not confirm project inclusion of " + file + ". " +
        "The generated file was preserved. Check Visual Studio and the .csproj before retrying.", inner);
