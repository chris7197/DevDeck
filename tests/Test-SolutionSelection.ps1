$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build .\chris7197.DevDeck.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --no-build --project .\src\chris7197.DevDeck.Cli -- --selftest-solution-selection
    if ($LASTEXITCODE -ne 0) { throw 'Solution selection tests failed.' }
    Write-Host 'Passed: focused VS, picker, cancellation, paging, and selected-instance Save All.' -ForegroundColor Green
    Write-Host 'UI preview (no Git or project modifications):'
    Write-Host '  dotnet run --project .\src\chris7197.DevDeck.Cli -- --choose-solution'
}
finally { Pop-Location }
