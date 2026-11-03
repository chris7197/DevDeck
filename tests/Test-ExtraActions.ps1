$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet run --project .\src\chris7197.DevDeck.Cli -- --selftest-extra-actions
    if ($LASTEXITCODE -ne 0) { throw 'DevDeck extra action self-test failed.' }
    $manifest = Get-Content .\plugin\manifest.json -Raw | ConvertFrom-Json
    $ids = @($manifest.Actions | ForEach-Object { $_.UUID })
    foreach ($suffix in @('teamschannel','openvscode','opensolution')) {
        if ($ids -notcontains "com.chris7197.devdeck.$suffix") { throw "Missing action $suffix in manifest" }
    }
    if (!(Test-Path .\plugin\property-inspector.html)) { throw 'Missing Property Inspector.' }
    Write-Host 'Manifest/Property Inspector checks passed.' -ForegroundColor Green
}
finally { Pop-Location }
