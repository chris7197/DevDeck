# Transactional installer. This script can also be tested with a fake package without dotnet.
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [string]$InstallPath = '',
    [switch]$ResetSettings,
    [switch]$SimulateFailureAfterBackup,
    [switch]$SkipRunningProcessCheck
)
$ErrorActionPreference = 'Stop'
$pluginFolder = 'com.chris7197.devdeck.sdPlugin'
if (-not $InstallPath) { $InstallPath = Join-Path $env:APPDATA 'HotSpot/StreamDock/plugins' }
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
$InstallPath = [IO.Path]::GetFullPath($InstallPath)
$dest = Join-Path $InstallPath $pluginFolder
$guid = [guid]::NewGuid().ToString('N')
$stage = Join-Path $InstallPath ("$pluginFolder.stage-$guid")
$backup = Join-Path $InstallPath ("$pluginFolder.backup-$guid")
$oldMoved = $false
$newMoved = $false

function Confirm-Package([string]$Folder) {
    foreach ($file in @('manifest.json', 'settings.json', 'chris7197.DevDeck.Plugin.exe',
                       'overlay/chris7197.DevDeck.Overlay.exe', 'property-inspector.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Folder $file) -PathType Leaf)) {
            throw "Invalid DevDeck package: missing $file"
        }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $Folder 'manifest.json') -Raw | ConvertFrom-Json
    $null = Get-Content -LiteralPath (Join-Path $Folder 'settings.json') -Raw | ConvertFrom-Json
    $localConfig = Join-Path $Folder 'settings.local.json'
    if (Test-Path -LiteralPath $localConfig -PathType Leaf) {
        $null = Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json
    }
    if (-not $manifest.Actions -or $manifest.Actions.Count -eq 0) {
        throw 'Invalid DevDeck package: manifest has no actions.'
    }
}

try {
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
        throw "Published source folder not found: $SourcePath"
    }
    Confirm-Package $SourcePath
    # Renames may fail on Windows when the native plugin or overlay is still running.
    if (-not $SkipRunningProcessCheck -and
        (Get-Process -Name 'chris7197.DevDeck.Plugin','chris7197.DevDeck.Overlay' -ErrorAction SilentlyContinue)) {
        throw 'Quit SOOMFON/Stream Dock and any DevDeck overlay before installing.'
    }
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    # Stage a COMPLETE install before altering the old one.
    Copy-Item -LiteralPath $SourcePath -Destination $stage -Recurse -Force
    if (-not $ResetSettings -and (Test-Path -LiteralPath (Join-Path $dest 'settings.json') -PathType Leaf)) {
        Copy-Item -LiteralPath (Join-Path $dest 'settings.json') -Destination (Join-Path $stage 'settings.json') -Force
        Write-Host 'Preserving existing installed settings.json.'
    }
    if (-not $ResetSettings -and -not (Test-Path -LiteralPath (Join-Path $stage 'settings.local.json') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $dest 'settings.local.json') -PathType Leaf)) {
        Copy-Item -LiteralPath (Join-Path $dest 'settings.local.json') -Destination (Join-Path $stage 'settings.local.json') -Force
        Write-Host 'Preserving existing private settings.local.json.' 
    }
    Confirm-Package $stage

    if (Test-Path -LiteralPath $dest) {
        Move-Item -LiteralPath $dest -Destination $backup -ErrorAction Stop
        $oldMoved = $true
    }
    if ($SimulateFailureAfterBackup) { throw 'Simulated installation failure (rollback test).' }
    Move-Item -LiteralPath $stage -Destination $dest -ErrorAction Stop
    $newMoved = $true
    Confirm-Package $dest
    if ($oldMoved) {
        try { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Installed new version, but old backup cleanup failed at ${backup}: $_" }
    }
    Write-Host "Installed DevDeck to: $dest"
    Write-Host 'Restart SOOMFON/Stream Dock to load the updated plugin.'
}
catch {
    $failure = $_
    # If the new install exists but final verification failed, remove it before restoring.
    if ($oldMoved -and (Test-Path -LiteralPath $backup)) {
        try {
            if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
            Move-Item -LiteralPath $backup -Destination $dest -ErrorAction Stop
            Write-Warning "Installation failed; previous DevDeck installation restored: $failure"
        }
        catch {
            Write-Warning "ROLLBACK FAILED. Original plugin is preserved at $backup. Restore it manually. $_"
        }
    }
    elseif ($newMoved -and (Test-Path -LiteralPath $dest)) {
        try { Remove-Item -LiteralPath $dest -Recurse -Force } catch { }
    }
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Never delete $backup here: retain it if rollback or cleanup failed.
}
