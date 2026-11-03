# DevDeck

**A customisable developer productivity toolkit for Windows, built with .NET 8 and designed for SOOMFON / Stream Dock devices.**

DevDeck brings common development tasks together into a configurable shortcut deck, helping reduce repetitive actions and streamline everyday workflows.

It integrates with development tools such as Visual Studio, SQL Server Management Studio (SSMS), Git, and other Windows applications.

DevDeck includes a Stream Dock plugin, a system tray application, a command-line host, and a desktop simulator for testing actions without a physical device.

## Features

- **Customisable shortcut deck** — Configure buttons, actions, icons, and profiles to suit your workflow.
- **Application-aware profiles** — Automatically switch profiles based on the active application or window.
- **Visual Studio integration** — Quickly access common IDE commands and development tasks.
- **SQL Server tools** — Shortcuts for SSMS, SQL formatting, and database-related workflows.
- **Git operations** — Perform common source control operations directly from your deck.
- **Log inspection** — Locate and inspect application logs using configurable project mappings.
- **Workflow automation** — Combine repetitive development tasks into convenient actions.
- **Diagnostics** — Tools for investigating issues and inspecting application behaviour.
- **Desktop simulator** — Test and interact with DevDeck without needing a connected Stream Dock.

## Requirements

- Windows
- .NET 8 SDK
- Visual Studio with the Windows desktop development workload (recommended)
- SOOMFON / Stream Dock software and compatible hardware (for physical deck integration)

The simulator and command-line components can also be used without a physical device.

## Getting Started

### 1. Clone the repository

```powershell
git clone https://github.com/YOUR_USERNAME/DevDeck.git
cd DevDeck
```

Replace `YOUR_USERNAME` with your GitHub username.

### 2. Build the solution

Open `Chris7197.DevDeck.slnx` in Visual Studio, or build using the .NET CLI:

```powershell
dotnet build Chris7197.DevDeck.slnx
```

### 3. Publish and install

Run the included PowerShell publishing script:

```powershell
.\scripts\Publish-Plugin.ps1 -Install -InstallTray
```

This builds and installs the SOOMFON plugin and DevDeck tray application.

The generated plugin package is located at:

```text
dist/com.chris7197.devdeck.sdPlugin
```

The `dist` directory is excluded from Git.

**Publishing options**

| Option | Description |
|---|---|
| `-Install` | Install the generated Stream Dock plugin |
| `-InstallTray` | Install the DevDeck tray application |
| `-ResetSettings` | Replace existing installed settings with the published defaults |

Existing settings are preserved during normal installations. Use `-ResetSettings` only when you intentionally want to restore the published configuration.

Restart the Stream Dock software after installation if the plugin does not appear immediately.

## Configuration

DevDeck uses JSON configuration files to customise its behaviour without modifying or recompiling source code.

The configuration files are located in the `plugin` directory.

| File | Purpose | Commit to Git? |
|---|---|---|
| `settings.json` | Default application configuration | Yes |
| `settings.local.example.json` | Example local configuration | Yes |
| `settings.local.json` | Personal overrides and environment-specific settings | No |

### Creating your local configuration

Copy the example configuration:

```powershell
Copy-Item `
    .\plugin\settings.local.example.json `
    .\plugin\settings.local.json
```

Edit `settings.local.json` to configure DevDeck for your own environment.

Local settings override the defaults in `settings.json`.

Configuration merging follows these rules:

- Only include settings you want to override.
- Nested JSON objects are merged.
- Arrays replace the corresponding default arrays entirely.
- Array ordering is preserved, which is important for rule-based configurations.

### Example configuration

```json
{
  "LogMappings": [
    {
      "PathFilter": "*ExampleApp.sln",
      "RelativePath": "logs/error.log"
    }
  ],
  "MigrationTargets": [
    {
      "PathFilter": "*ExampleApp.sln",
      "ProjectPath": "src/Migrations/Migrations.csproj"
    }
  ],
  "ProfileRules": [
    {
      "Profile": "VisualStudio",
      "Process": "devenv"
    },
    {
      "Profile": "SqlServer",
      "Process": "ssms"
    },
    {
      "Profile": "Browser",
      "Process": "msedge",
      "TitleContains": "Developer Dashboard"
    }
  ]
}
```

This example demonstrates three configurable behaviours:

**Log mappings**

Associate solutions with specific log files or directories.

`PathFilter` supports wildcard matching against the full solution path. `RelativePath` is resolved relative to the solution's directory.

The first matching mapping is used.

When the mapped location is a directory, DevDeck uses `LogFilePattern` to identify matching files and selects the most recently modified file.

**Migration targets**

Associate solutions with their corresponding migration projects, allowing development shortcuts to locate the appropriate project automatically.

**Application profiles**

Automatically activate different button configurations based on the foreground application or window title.

For example, DevDeck can display Visual Studio shortcuts when Visual Studio is active, then switch to SQL shortcuts when SSMS becomes the foreground application.

### Additional configuration

Other configurable areas include:

- Application-specific profiles and button presets
- Microsoft Teams integration settings
- Database diagnostic targets
- Database flight recorder configuration
- Project-specific investigation SQL templates

SQL investigation templates can be configured through `InvestigationSqlLookups.ProductCode` and `InvestigationSqlLookups.AccessoryCode`.

Templates support a `{code}` placeholder. DevDeck generates the corresponding SQL for inspection but does not execute these queries automatically.

### Configuration during development and installation

**Stream Dock plugin**

The publishing script includes `settings.local.json` in the generated plugin package when the file exists.

Normal installations preserve the existing installed configuration.

**CLI and simulator**

During a build, the local configuration is copied alongside the relevant executable when available.

Rebuild the project after changing the source configuration.

**Installed configuration**

You can also edit `settings.local.json` directly in the installed plugin directory.

The legacy filename `settings.development.json` is not loaded. Use `settings.local.json` for personal configuration.

## Development and Testing

DevDeck is built with C# and .NET 8.

The solution includes reliability tests and PowerShell-based testing utilities.

Run the reliability tests:

```powershell
dotnet run --project .\tests\Chris7197.DevDeck.ReliabilityTests
```

Additional tests can be executed individually:

```powershell
.\tests\Test-LatestErrorLog.ps1
.\tests\Test-LastError.ps1
.\tests\Test-WorkflowActions.ps1
```

Tests should be run on Windows with the .NET 8 SDK installed.

## Repository Security

DevDeck supports local, environment-specific configuration to avoid hard-coding machine paths, credentials, and other private values into the source.

The repository's `.gitignore` excludes local configuration files, build output, generated packages, and other development artefacts.

When contributing or publishing changes:

- Keep personal configuration in `settings.local.json`.
- Do not commit passwords, access tokens, connection strings, or credentials.
- Avoid including private application paths, proprietary code, or sensitive diagnostic output.
- Review staged changes before committing.

A source-checking utility is included:

```powershell
.\scripts\Check-PublicSource.ps1
```

You can optionally create `plugin/.private-source-markers.txt` containing identifiers that should never appear in committed source. Add one identifier per line. The file is ignored by Git.

For additional verification:

```powershell
git status
git diff --cached
```

**Note:** Git ignore rules prevent new untracked files from being added automatically, but do not remove files that have already been committed.

## Contributing

Suggestions, bug reports, and improvements are welcome.

If you encounter an issue or have an idea for a new developer shortcut, please open a GitHub issue.

For code contributions, please keep changes focused, avoid introducing environment-specific assumptions, and run the relevant tests before submitting a pull request.

---

**DevDeck** — Making everyday development tasks a little quicker.