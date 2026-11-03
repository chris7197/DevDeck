$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('DevDeckLiveStatus-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
try {
    $solution = Join-Path $fixture 'DevDeck.LogFixture.sln'
    Set-Content -Path $solution -Value @'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
'@
    $logFolder = Join-Path $fixture 'Logs'
    New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
    Set-Content -Path (Join-Path $logFolder 'run.log') -Value "INFO: starting`nERROR: Test diagnostic failure"
    $cli = Join-Path $root 'src/chris7197.DevDeck.Cli/chris7197.DevDeck.Cli.csproj'
    $output = & dotnet run --project $cli -- --show-status $solution 2>&1
    if ($LASTEXITCODE -ne 0) { throw "CLI failed: $output" }
    $text = $output -join "`n"
    if ($text -notmatch 'Latest Log:') { throw 'Latest Log status was not generated.' }
    if ($text -notmatch 'Last Error: Error found') { throw "Last error not detected: $text" }
    if ($text -notmatch 'New Migration: Unavailable') { throw 'Expected unavailable migration target in fixture.' }
    Write-Host 'PASS: Live status providers detected log activity and last error.' -ForegroundColor Green
    Write-Host $text
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
