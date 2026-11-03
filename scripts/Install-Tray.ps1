# Install the standalone tray companion outside of the Stream Dock plugin folder.
# The tray watches the shared log even when the SOOMFON software is not running.
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'chris7197/DevDeck/Tray'
$stage = "$target.stage-$([guid]::NewGuid().ToString('N'))"
$backup = "$target.backup-$([guid]::NewGuid().ToString('N'))"
$exe = 'chris7197.DevDeck.Tray.exe'
if (-not (Test-Path -LiteralPath (Join-Path $SourcePath $exe) -PathType Leaf)) {
    throw "Tray publish missing $exe"
}
if (Get-Process -Name 'chris7197.DevDeck.Tray' -ErrorAction SilentlyContinue) {
    throw 'Please right-click the DevDeck system tray icon and choose Exit DevDeck Tray before upgrading.'
}
$parent = Split-Path $target -Parent
New-Item -ItemType Directory -Force $parent | Out-Null
$oldMoved = $false
$newMoved = $false
try {
    Copy-Item -LiteralPath $SourcePath -Destination $stage -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $stage $exe))) { throw 'Staged tray is incomplete.' }
    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination $backup
        $oldMoved = $true
    }
    Move-Item -LiteralPath $stage -Destination $target
    $newMoved = $true
    if (-not (Test-Path -LiteralPath (Join-Path $target $exe))) { throw 'Installed tray is incomplete.' }
    if ($oldMoved) {
        try { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Tray backup remains at $backup; $_" }
    }
    Write-Host "Installed DevDeck Tray to $target"
    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $target $exe) }
}
catch {
    if ($oldMoved -and (Test-Path -LiteralPath $backup)) {
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Move-Item -LiteralPath $backup -Destination $target
    }
    elseif ($newMoved -and (Test-Path -LiteralPath $target)) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
