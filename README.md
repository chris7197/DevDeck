# DevDeck

Windows / .NET 8 developer shortcut deck for SOOMFON / Stream Dock, with a tray app,
command-line host and simulator. Includes configurable profiles, Visual Studio/SSMS
shortcuts, Git operations, SQL formatting, log inspection and diagnostics.

## Build

Open `Chris7197.DevDeck.slnx` in Visual Studio with the .NET 8 SDK / Windows desktop
workload, or run `dotnet build Chris7197.DevDeck.slnx` on Windows.

Publish the SOOMFON plugin with:

```powershell
.\scripts\Publish-Plugin.ps1 -Install -InstallTray
```

The publish script creates `dist/com.chris7197.devdeck.sdPlugin`, which is ignored by
Git. `-Install` installs the plugin and preserves your existing installed settings;
`-ResetSettings` intentionally replaces them.

## Private configuration (do not commit)

`plugin/settings.json` is the **public, safe default configuration**.

Create `plugin/settings.local.json` from
`plugin/settings.local.example.json` and add your own company/project-specific
settings there. The local file is ignored by Git, and its values override the public
settings. Top-level keys can be omitted: you only need the ones you want to change.
Nested JSON objects merge; arrays replace the public list in full (including rule order).

For example:

```json
{
  "LogMappings": [
    { "PathFilter": "*MyApp.sln", "RelativePath": "logs/error.log" }
  ],
  "MigrationTargets": [
    { "PathFilter": "*MyApp.sln", "ProjectPath": "src/MyMigrator/MyMigrator.csproj" }
  ],
  "ProfileRules": [
    { "Profile": "VisualStudio", "Process": "devenv" },
    { "Profile": "SqlServer", "Process": "ssms" },
    { "Profile": "BrowserBi", "Process": "msedge", "TitleContains": "My Dashboard" }
  ]
}
```

`LogMappings` accepts a wildcard `PathFilter` matching the **full solution path**;
`RelativePath` is a file or folder relative to the directory containing that solution.
The first matching entry wins. Use `"*MyApp*"` for any solution filename containing
that text. When a mapping points to a directory, `LogFilePattern` controls which
files to search and DevDeck opens the most recently modified matching log.

Other settings you can keep private include `Teams.TenantId`, `Teams.ClientId`,
`DbFlightRecorderTargets`, `DbFlightRecorderConfigMappings`, `ButtonPresets`,
and per-application `Profiles`. For project-specific product/accessory SQL, add
`InvestigationSqlLookups.ProductCode` / `InvestigationSqlLookups.AccessoryCode`
templates with a `{code}` placeholder; DevDeck does **not** execute those queries.

### Where the private config goes

- **SOOMFON plugin:** `scripts/Publish-Plugin.ps1` copies `plugin/settings.local.json`
  into the ignored `dist` package, and the installer preserves existing private
  settings on subsequent upgrades. The running plugin reads both files.
- **CLI/simulator in Visual Studio:** the project files copy the local config next
  to the executable when it exists during build. Rebuild after editing the local file.
- **Installed plugin:** changing `settings.local.json` in the installed plugin folder
  is also supported; keep a backup if your plugin installation is reset.

The older `settings.development.json` filename is still Git-ignored for safety,
but is **not loaded**. Use `settings.local.json`.

## Before publishing to GitHub

Optionally add `plugin/.private-source-markers.txt` (one confidential identifier
per line, ignored by Git) to let the scan catch your specific identifiers.
Run `scripts/Check-PublicSource.ps1` (requires Git) and check `git status` and
`git diff --cached` before pushing. Do not commit `dist`, binary output, private
settings, exported reports, customer code, connection strings, or your own `.env`.
If a private file has **ever** been committed, `.gitignore` does not erase it from
Git history. Review repository history separately. Do not publicly release code
unless you have the rights to do so.

## Tests

On Windows with .NET 8, run:

```powershell
dotnet run --project .\tests\Chris7197.DevDeck.ReliabilityTests
.\tests\Test-LatestErrorLog.ps1
.\tests\Test-LastError.ps1
.\tests\Test-WorkflowActions.ps1
```
