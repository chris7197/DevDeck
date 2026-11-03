$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet run --project .\src\chris7197.DevDeck.Cli -- --selftest-db-flight-config
    if ($LASTEXITCODE -ne 0) { throw 'DB Flight config resolver offline tests failed.' }
    Write-Host 'DB Flight configuration test: PASS (no database connections).' -ForegroundColor Green
}
finally { Pop-Location }
