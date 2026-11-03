# Scan Git-candidate files to catch accidental private data before commit.
# This is an extra guard, not a substitute for reviewing staged changes/history.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required.' }
    # Optional private marker list, stored in an ignored file (one marker per line).
    $markerFile = Join-Path $root 'plugin/.private-source-markers.txt'
    $markers = @()
    if (Test-Path -LiteralPath $markerFile -PathType Leaf) {
        $markers = @(Get-Content -LiteralPath $markerFile | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') })
    }
    $forbidden = if ($markers.Count) { '(?i)' + (($markers | ForEach-Object { [regex]::Escape($_) }) -join '|') } else { $null }
    $secretPatterns = '(?i)(password|clientsecret|api[_-]?key)\s*["'':=]+\s*[^"''\s,}]{8,}'
    # Includes untracked-but-not-ignored files because `git add .` would include those.
    $paths = @(& git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to list Git files.' }
    $issues = New-Object System.Collections.Generic.List[string]
    foreach ($file in $paths) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        if ($file -match '(?i)(^|/|\\)(settings\.local\.json|settings\.development\.json|config\.local\.json|\.env)$') {
            $issues.Add("Tracked/private file: $file"); continue
        }
        if ($file -match '(?i)\.(png|jpg|jpeg|ico|dll|exe|zip|pdf)$') { continue }
        $patterns = @($secretPatterns)
        if ($forbidden) { $patterns += $forbidden }
        $matches = Select-String -LiteralPath $file -Pattern $patterns -AllMatches -ErrorAction SilentlyContinue
        foreach ($match in $matches) {
            $issues.Add("$($file):$($match.LineNumber): $($match.Line.Trim())")
        }
    }
    if ($issues.Count -gt 0) {
        $issues | ForEach-Object { Write-Warning $_ }
        throw "Found $($issues.Count) potential public-source leak(s). Review before pushing."
    }
    Write-Host 'PASS: no configured private markers or suspicious secret assignments found in Git-candidate text files.' -ForegroundColor Green
} finally { Pop-Location }
