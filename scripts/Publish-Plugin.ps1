param(
    [string]$Runtime = 'win-x64',
    [string]$InstallPath = '',
    [switch]$Install,
    [switch]$ResetSettings,
    [switch]$InstallTray
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pluginFolder = 'com.chris7197.devdeck.sdPlugin'
$out = Join-Path $root (Join-Path 'dist' $pluginFolder)
# Stale published binaries from a previous release must not enter the new package.
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$project = Join-Path $root 'src/chris7197.DevDeck.Plugin/chris7197.DevDeck.Plugin.csproj'
dotnet publish $project -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The overlay is a separate, minimal WinForms executable communicating over stdio.
# Keep it in its own subfolder to prevent runtime files from colliding with the plugin.
$overlayProject = Join-Path $root 'src/chris7197.DevDeck.Overlay/chris7197.DevDeck.Overlay.csproj'
$overlayOut = Join-Path $out 'overlay'
New-Item -ItemType Directory -Force $overlayOut | Out-Null
dotnet publish $overlayProject -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None -o $overlayOut
if ($LASTEXITCODE -ne 0) { throw 'DevDeck overlay publish failed.' }


Copy-Item (Join-Path $root 'plugin/manifest.json') (Join-Path $out 'manifest.json') -Force
Copy-Item (Join-Path $root 'plugin/settings.json') (Join-Path $out 'settings.json') -Force
# Local settings are intentionally not tracked. Carry them into the *ignored* package
# as a separate override, never merge private values into the public template.
$localSettings = Join-Path $root 'plugin/settings.local.json'
if (Test-Path -LiteralPath $localSettings -PathType Leaf) {
    $null = Get-Content -LiteralPath $localSettings -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $localSettings -Destination (Join-Path $out 'settings.local.json') -Force
    Write-Host 'Included local settings override (private; distribution output is Git-ignored).'
}

Copy-Item (Join-Path $root 'plugin/property-inspector.html') (Join-Path $out 'property-inspector.html') -Force
New-Item -ItemType Directory -Force (Join-Path $out 'scripts') | Out-Null
Copy-Item (Join-Path $root 'scripts/Format-CSharp.ps1') (Join-Path $out 'scripts/Format-CSharp.ps1') -Force
Copy-Item (Join-Path $root 'scripts/csharpier-defaults.json') (Join-Path $out 'scripts/csharpier-defaults.json') -Force
New-Item -ItemType Directory -Force (Join-Path $out 'images') | Out-Null
Copy-Item (Join-Path $root 'plugin/images/*') (Join-Path $out 'images') -Force
Write-Host "Packaged plugin: $out"

if ($Install) {
    & (Join-Path $PSScriptRoot 'Install-Plugin.ps1') -SourcePath $out `
        -InstallPath $InstallPath -ResetSettings:$ResetSettings
    if (-not $?) { throw 'DevDeck plugin install failed.' }
}

# Optional independent tray application; it is installed outside the SOOMFON plugin
# directory so the device can restart without interrupting log monitoring.
if ($InstallTray) {
    $trayProject = Join-Path $root 'src/chris7197.DevDeck.Tray/chris7197.DevDeck.Tray.csproj'
    $trayOut = Join-Path $root 'dist/DevDeck.Tray'
    if (Test-Path -LiteralPath $trayOut) { Remove-Item -LiteralPath $trayOut -Recurse -Force }
    dotnet publish $trayProject -c Release -r $Runtime --self-contained true `
      -p:PublishSingleFile=true -p:DebugType=None -o $trayOut
    if ($LASTEXITCODE -ne 0) { throw 'DevDeck tray publish failed.' }
    & (Join-Path $PSScriptRoot 'Install-Tray.ps1') -SourcePath $trayOut
    if (-not $?) { throw 'DevDeck tray install failed.' }
}
