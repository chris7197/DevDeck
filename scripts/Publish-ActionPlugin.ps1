# Publish a single independently deployable action DLL + its config and manifest.
# This does NOT overwrite live user configuration or install into a running Stream Dock.
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('shortcuts', 'git', 'visualstudio', 'teams', 'diagnostics', 'investigation', 'dbflight')]
    [string]$Name,
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$names = @{
    shortcuts = 'chris7197.DevDeck.Shortcuts'
    git = 'chris7197.DevDeck.Git'
    visualstudio = 'chris7197.DevDeck.VisualStudio'
    teams = 'chris7197.DevDeck.Teams'
    diagnostics = 'chris7197.DevDeck.Diagnostics'
    investigation = 'chris7197.DevDeck.Investigation'
    dbflight = 'chris7197.DevDeck.DbFlight'
}
$projectName = $names[$Name]
$source = Join-Path $root "src/$projectName"
$output = Join-Path $root "dist/action-plugins/$Name"
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& dotnet publish (Join-Path $source "$projectName.csproj") -c Release -r $Runtime `
    --self-contained false -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw "Publish of $Name action plugin failed." }
Copy-Item (Join-Path $source 'plugin.json') $output -Force
Copy-Item (Join-Path $source 'config.json') $output -Force
Copy-Item (Join-Path $source 'config.schema.json') $output -Force
Write-Host "Published $Name plugin to: $output"
Write-Host 'To upgrade an installed plugin, stop Stream Dock; keep the installed config.json and replace only DLLs/dependencies and plugin.json.'
