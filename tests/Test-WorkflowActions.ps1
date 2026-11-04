# Structural checks (no running Visual Studio, SSMS, CSharpier or Stream Dock required).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $root 'plugin/manifest.json') -Raw | ConvertFrom-Json
$settings = Get-Content -LiteralPath (Join-Path $root 'plugin/settings.json') -Raw | ConvertFrom-Json
$expected = @(
    'com.chris7197.devdeck.closeothers',
    'com.chris7197.devdeck.csharpier.format',
    'com.chris7197.devdeck.sql.format'
)
$ids = @($manifest.Actions | ForEach-Object UUID)
foreach ($id in $expected) {
    if ($ids -notcontains $id) { throw "Missing manifest action: $id" }
}
if (($ids | Sort-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate manifest action UUIDs.' }
if (@($ids | Where-Object { $_ -like '*closeothers*' }).Count -ne 1) {
    throw 'Expected exactly one Close Others action in the manifest.'
}
$runtime = Get-Content -LiteralPath (Join-Path $root 'src/chris7197.DevDeck.Core/DevDeckRuntime.cs') -Raw
if ($runtime -notmatch 'OldVisualStudioActionId' -or $runtime -notmatch 'OldSsmsActionId') {
    throw 'Installed settings compatibility aliases missing.'
}

foreach ($entry in $manifest.Actions) {
    if (-not (Test-Path -LiteralPath (Join-Path $root ('plugin/' + $entry.Icon)) -PathType Leaf)) {
        throw "Missing action icon for $($entry.UUID)"
    }
}
if ($settings.Profiles.VisualStudio[11] -ne 'com.chris7197.devdeck.closeothers' -or
    $settings.Profiles.VisualStudio[12] -ne 'com.chris7197.devdeck.csharpier.format' -or
    $settings.Profiles.VisualStudio[13] -ne 'com.chris7197.devdeck.sql.format') {
    throw 'Visual Studio sample profile positions changed.'
}
if ($settings.Profiles.SqlServer[11] -ne 'com.chris7197.devdeck.closeothers' -or
    $settings.Profiles.SqlServer[12] -ne 'com.chris7197.devdeck.sql.format' -or
    $settings.Profiles.SqlServer[5]) {
    throw 'SSMS sample profile position changed.'
}
$format = Get-Content -LiteralPath (Join-Path $root 'scripts/Format-CSharp.ps1') -Raw
$defaults = Get-Content -LiteralPath (Join-Path $root 'scripts/csharpier-defaults.json') -Raw | ConvertFrom-Json
if (-not ($format -match 'csharpier') -or $defaults.printWidth -ne 140) { throw 'CSharpier configuration missing.' }
# C# formatter must never equate exit code 0 with an actual edit.
$csharpOperation = Get-Content -LiteralPath (Join-Path $root 'src/chris7197.DevDeck.Core/Actions/FormatCSharpOperation.cs') -Raw
$pluginCode = Get-Content -LiteralPath (Join-Path $root 'src/chris7197.DevDeck.Plugin/Program.cs') -Raw
if ($csharpOperation -notmatch 'HashAsync' -or $csharpOperation -notmatch 'NoChanges: true' -or
    $pluginCode -notmatch 'result.NoChanges' -or $format -notmatch '--no-cache') {
    throw 'C# formatting change detection or no-change feedback is missing.'
}

$publish = Get-Content -LiteralPath (Join-Path $root 'scripts/Publish-Plugin.ps1') -Raw
if ($publish -notmatch 'Format-CSharp.ps1' -or $publish -notmatch 'csharpier-defaults.json') {
    throw 'Publish script does not bundle the formatter.'
}
if ($runtime -notmatch 'FormatSqlOperation' -or $runtime -notmatch 'chris7197SqlFormatter') {
    throw 'SQL formatter not registered in runtime.'
}
Write-Host 'DevDeck workflow source/manifest/profiles/package checks: PASS' -ForegroundColor Green
