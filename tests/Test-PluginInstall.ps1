# Offline transactional-installer test. Needs only Windows PowerShell/PowerShell, not dotnet.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path ([IO.Path]::GetTempPath()) ('DevDeckInstallTest-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $dir 'package'
$targetRoot = Join-Path $dir 'installed'
$dest = Join-Path $targetRoot 'com.chris7197.devdeck.sdPlugin'
function Package([string]$path, [string]$version) {
    New-Item -ItemType Directory -Force (Join-Path $path 'overlay') | Out-Null
    Set-Content (Join-Path $path 'manifest.json') '{"Actions":[{"UUID":"test"}]}'
    Set-Content (Join-Path $path 'settings.json') '{"Keep":"new"}'
    Set-Content (Join-Path $path 'chris7197.DevDeck.Plugin.exe') $version
    Set-Content (Join-Path $path 'overlay/chris7197.DevDeck.Overlay.exe') 'overlay'
    Set-Content (Join-Path $path 'property-inspector.html') 'inspector'
}
try {
    Package $source 'v2'
    Package $dest 'v1'
    Set-Content (Join-Path $dest 'settings.json') '{"Keep":"user"}'
    $script = Join-Path $root 'scripts/Install-Plugin.ps1'
    $failed = $false
    try { & $script -SourcePath $source -InstallPath $targetRoot -SimulateFailureAfterBackup -SkipRunningProcessCheck } catch { $failed = $true }
    if (-not $failed) { throw 'Failure-injection test did not fail.' }
    if ((Get-Content (Join-Path $dest 'chris7197.DevDeck.Plugin.exe') -Raw).Trim() -ne 'v1') {
        throw 'Old version was not restored after failure.'
    }
    if (-not ((Get-Content (Join-Path $dest 'settings.json') -Raw) -match 'user')) {
        throw 'Custom settings lost after failure.'
    }
    & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck
    if ((Get-Content (Join-Path $dest 'chris7197.DevDeck.Plugin.exe') -Raw).Trim() -ne 'v2') {
        throw 'New version not installed.'
    }
    if (-not ((Get-Content (Join-Path $dest 'settings.json') -Raw) -match 'user')) {
        throw 'Custom settings not preserved on successful upgrade.'
    }
    Write-Host 'PASS: installer rollback and preservation of existing settings.'
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
