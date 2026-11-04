# Add only the Project Developer Command Prompt integration.
# Does not replace DeckSettings.cs, existing runtime/VS integration classes, or user settings wholesale.
[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$InstalledSettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$actionId = 'com.chris7197.devdeck.projectdevcmd'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$bundleRoot = Split-Path $PSScriptRoot -Parent

function Read-Utf8([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}

function Save-Changed([string]$path, [string]$oldText, [string]$newText) {
    if ($oldText -ceq $newText) { return }
    $backup = "$path.before-projectdevcmd.bak"
    if (!(Test-Path -LiteralPath $backup)) {
        [System.IO.File]::WriteAllText($backup, $oldText, $script:utf8)
    }
    [System.IO.File]::WriteAllText($path, $newText, $script:utf8)
    Write-Host "Updated: $path"
}

function Update-VisualStudioProfile([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $old = Read-Utf8 $path
    $match = [regex]::Match($old, '(?i)"VisualStudio"\s*:\s*\[(?<items>[^\]]*)\]')
    if (!$match.Success) {
        Write-Warning "No VisualStudio profile array in $path; please assign the action manually."
        return
    }
    $body = $match.Groups['items'].Value
    $tokens = [regex]::Matches($body, '"[^"\r\n]*"|\bnull\b', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $values = @($tokens | ForEach-Object { $_.Value.Trim('"') })
    if ($values -contains $script:actionId) { return }
    if ($tokens.Count -gt 15) {
        Write-Warning "More than 15 Visual Studio slots in $path; please assign the action manually."
        return
    }
    $newBody = $null
    $nl = if ($old.Contains("`r`n")) { "`r`n" } else { "`n" }
    if ($tokens.Count -lt 15) {
        # Preserve all existing slot positions, including gaps. Prefer an empty slot 15.
        $trimmed = $body.TrimEnd()
        $tail = $body.Substring($trimmed.Length)
        if ($trimmed.Length -gt 0 -and !$trimmed.EndsWith(',')) { $trimmed += ',' }
        $added = @()
        for ($i = $tokens.Count; $i -lt 14; $i++) { $added += 'null' }
        $added += '"' + $script:actionId + '"'
        $newBody = $trimmed + $nl + '      ' + ($added -join (',' + $nl + '      ')) + $tail
    }
    else {
        # All 15 slots exist. Use a free slot if any; NEVER replace an assigned action.
        $free = -1
        if ($values[14] -eq 'null') { $free = 14 }
        else {
            for ($i = 0; $i -lt 15; $i++) {
                if ($values[$i] -eq 'null') { $free = $i; break }
            }
        }
        if ($free -lt 0) {
            Write-Warning "All 15 Visual Studio slots in $path are occupied. Action registered but not assigned."
            return
        }
        $token = $tokens[$free]
        $newBody = $body.Remove($token.Index, $token.Length).Insert($token.Index, '"' + $script:actionId + '"')
    }
    $newText = $old.Remove($match.Groups['items'].Index, $body.Length).Insert($match.Groups['items'].Index, $newBody)
    Save-Changed $path $old $newText
}

# Validate the checkout BEFORE changing any of its files.
$runtimePath = Join-Path $ProjectRoot 'src\Chris7197.DevDeck.Core\DevDeckRuntime.cs'
$manifestPath = Join-Path $ProjectRoot 'plugin\manifest.json'
if (!(Test-Path -LiteralPath $runtimePath) -or !(Test-Path -LiteralPath $manifestPath)) {
    throw "Not a DevDeck source root: $ProjectRoot. Run from the extracted bundle and supply -ProjectRoot C:\path\to\DevDeck if needed."
}
$runtime = Read-Utf8 $runtimePath
if ([regex]::IsMatch($runtime, 'new\s+OpenProjectDeveloperPromptOperation\s*\(')) {
    $runtimeUpdated = [regex]::Replace($runtime,
        'new\s+OpenProjectDeveloperPromptOperation\s*\(\s*[^)]*\s*\)',
        'new OpenProjectDeveloperPromptOperation(VisualStudioCom)')
}
else {
    # Insert after the existing VS Code action to preserve all the user's other operations.
    $anchor = [regex]::Match($runtime, '(?m)^(?<indent>[ \t]*)new OpenInVsCodeOperation\([^\r\n]*\),')
    if (!$anchor.Success) { throw 'Cannot locate OpenInVsCodeOperation in DevDeckRuntime.cs; no changes applied.' }
    $insert = "`r`n" + $anchor.Groups['indent'].Value + 'new OpenProjectDeveloperPromptOperation(VisualStudioCom),'
    $runtimeUpdated = $runtime.Insert($anchor.Index + $anchor.Length, $insert)
}
if ($runtimeUpdated -notmatch 'new\s+OpenProjectDeveloperPromptOperation\s*\(\s*VisualStudioCom\s*\)') {
    throw 'Could not register the action with VisualStudioCom; no changes applied.'
}

$manifest = Read-Utf8 $manifestPath
$manifestUpdated = $manifest
if ($manifest -notmatch [regex]::Escape($actionId)) {
    $anchor = [regex]::Match($manifest, '"Actions"\s*:\s*\[')
    if (!$anchor.Success) { throw 'Cannot find Actions array in plugin manifest; no changes applied.' }
    $actionText = (Read-Utf8 (Join-Path $PSScriptRoot 'projectdevcmd.action.json')).Trim()
    $actionText = '    ' + (($actionText -split "`r?`n") -join "`r`n    ")
    $manifestUpdated = $manifest.Insert($anchor.Index + $anchor.Length, "`r`n$actionText,")
}
# Validate manifest if changed. Settings are edited in-place rather than replaced.
$null = $manifestUpdated | ConvertFrom-Json

# Copy ONLY new component files (never overwrite other DevDeck modules).
$files = @(
    'src\Chris7197.DevDeck.Core\Actions\OpenProjectDeveloperPromptOperation.cs',
    'src\Chris7197.DevDeck.Core\Integrations\ProjectDevCmdResolver.cs',
    'src\Chris7197.DevDeck.Core\Integrations\DeveloperCommandPromptLauncher.cs',
    'src\Chris7197.DevDeck.Core\Assets\Icons\project-devcmd.png',
    'plugin\images\project-devcmd-key.png',
    'plugin\images\project-devcmd-action.png'
)
foreach ($relative in $files) {
    $source = Join-Path $bundleRoot $relative
    $destination = Join-Path $ProjectRoot $relative
    $folder = Split-Path $destination -Parent
    if (!(Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    if (![string]::Equals([System.IO.Path]::GetFullPath($source),
            [System.IO.Path]::GetFullPath($destination), [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    Write-Host "Installed: $relative"
}
Save-Changed $runtimePath $runtime $runtimeUpdated
Save-Changed $manifestPath $manifest $manifestUpdated
Update-VisualStudioProfile (Join-Path $ProjectRoot 'plugin\settings.json')
Update-VisualStudioProfile (Join-Path $ProjectRoot 'plugin\settings.development.json')
if ($InstalledSettingsPath) { Update-VisualStudioProfile $InstalledSettingsPath }

Write-Host ''
Write-Host 'Project Developer Command Prompt integrated. Existing DeckSettings.cs and other settings were preserved.' -ForegroundColor Green
Write-Host 'Build DevDeck and run Publish-Plugin.ps1 -Install -InstallTray.'
Write-Host 'For existing installed settings, provide -InstalledSettingsPath or assign the action in the controller UI.'
