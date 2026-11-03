# Offline Phase 7 smoke checks; run from repository root on Windows with the .NET 8 SDK.
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..'))
$project = '.\src\chris7197.DevDeck.Cli'
$help = @(& dotnet run --project $project -- --help)
if ($LASTEXITCODE -ne 0) { throw 'CLI help failed.' }
foreach ($alias in @('--run', '--run-slot', '--repo', '--open-solution', '--teams-login',
                      '--db-flight-preview', '--investigate-text', '--last-error')) {
    if (-not ($help -match [regex]::Escape($alias))) { throw "Plugin CLI alias missing: $alias" }
}
$actions = @(& dotnet run --project $project -- --list-actions)
if ($LASTEXITCODE -ne 0) { throw 'CLI action discovery failed.' }
foreach ($action in @('com.chris7197.devdeck.syncmain',
                     'com.chris7197.devdeck.investigate', 'com.chris7197.devdeck.dbflight')) {
    if (-not ($actions -match [regex]::Escape($action))) { throw "Missing action $action" }
}
Write-Host 'PASS: CLI loads plugin-contributed commands and retained action IDs.'
