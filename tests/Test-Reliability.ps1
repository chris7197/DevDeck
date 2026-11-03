$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & dotnet build '.\chris7197.DevDeck.sln' -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'DevDeck solution failed to build.' }
    & dotnet run --no-build -c Debug --project '.\tests\chris7197.DevDeck.ReliabilityTests'
    if ($LASTEXITCODE -ne 0) { throw 'DevDeck reliability test harness failed.' }
    & '.\tests\Test-PluginInstall.ps1'
    if (-not $?) { throw 'Installer rollback test failed.' }
    Write-Host 'PASS: All DevDeck v2.7 reliability tests.'
}
finally { Pop-Location }
