$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build .\chris7197.DevDeck.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    dotnet run --no-build --project .\src\chris7197.DevDeck.Cli -- --selftest-overlay-contracts
    if ($LASTEXITCODE -ne 0) { throw 'Overlay self-tests failed.' }
    Write-Host 'All automated overlay checks passed.' -ForegroundColor Green
    Write-Host 'To test the actual UI, run:'
    Write-Host ' dotnet run --project .\src\chris7197.DevDeck.Cli -- --overlay-demo'
}
finally { Pop-Location }
