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
    # Use an isolated repository layout so the real private settings are never touched.
    $testRepo = Join-Path $dir 'repo'
    $testScripts = Join-Path $testRepo 'scripts'
    $testPlugin = Join-Path $testRepo 'plugin'
    New-Item -ItemType Directory -Force $testScripts, $testPlugin | Out-Null
    $script = Join-Path $testScripts 'Install-Plugin.ps1'
    Copy-Item -LiteralPath (Join-Path $root 'scripts/Install-Plugin.ps1') -Destination $script
    $local = Join-Path $testPlugin 'settings.local.json'
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
    function Assert-Settings([string]$expected) {
        $current = Get-Content -LiteralPath (Join-Path $dest 'settings.json') -Raw | ConvertFrom-Json
        if ($current.Keep -ne $expected) { throw "Expected settings '$expected', got '$($current.Keep)'." }
    }
    function Assert-LocalInstallFails([string]$expectedError, [switch]$Reset, [switch]$SimulateFailure) {
        $failed = $false
        try {
            & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck `
                -UseLocalSettings -ResetSettings:$Reset -SimulateFailureAfterBackup:$SimulateFailure
        }
        catch {
            if ($_.Exception.Message -notlike $expectedError) { throw }
            $failed = $true
        }
        if (-not $failed) { throw 'Expected local settings installation to fail.' }
    }

    Assert-LocalInstallFails 'Local settings file not found:*'
    Assert-Settings 'user'
    Set-Content -LiteralPath $local 'not valid json'
    Assert-LocalInstallFails '*'
    Assert-Settings 'user'
    Set-Content -LiteralPath $local '{"Keep":"local"}'
    Assert-LocalInstallFails 'UseLocalSettings and ResetSettings cannot be combined.' -Reset
    Assert-Settings 'user'
    Assert-LocalInstallFails 'Simulated installation failure*' -SimulateFailure
    Assert-Settings 'user'

    # A normal upgrade ignores local settings even when they exist.
    & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck
    Assert-Settings 'user'
    & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck -UseLocalSettings
    Assert-Settings 'local'
    & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck -ResetSettings
    Assert-Settings 'new'

    # The published package must remain unchanged, including on a fresh install.
    $packaged = Get-Content -LiteralPath (Join-Path $source 'settings.json') -Raw | ConvertFrom-Json
    if ($packaged.Keep -ne 'new') { throw 'Packaged settings were modified.' }
    Remove-Item -LiteralPath $dest -Recurse -Force
    & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck
    Assert-Settings 'new'
    Remove-Item -LiteralPath $dest -Recurse -Force
    & $script -SourcePath $source -InstallPath $targetRoot -SkipRunningProcessCheck -UseLocalSettings
    Assert-Settings 'local'
    Write-Host 'PASS: installer rollback, default settings, preservation and local settings selection.'
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
