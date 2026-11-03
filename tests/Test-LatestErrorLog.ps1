# Uses a disposable .sln and log folder to test the real .NET log resolver.
# Also validates the configurable direct-file mapping. Never opens a viewer.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('DevDeckLog-' + [guid]::NewGuid().ToString('N'))
function Find-Latest([string]$Solution) {
    Push-Location $root
    try {
        $output = & dotnet run --project '.\src\chris7197.DevDeck.Cli' -- --find-latest-log $Solution
        if ($LASTEXITCODE -ne 0) { throw "DevDeck CLI failed: $output" }
        $line = $output | Where-Object { $_ -like 'Latest log: *' } | Select-Object -Last 1
        if (-not $line) { throw "No 'Latest log:' line in output: $output" }
        return $line.Substring('Latest log: '.Length).Trim()
    } finally { Pop-Location }
}
try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'Logs') -Force | Out-Null
    $solution = Join-Path $fixture 'DevDeck.LogFixture.sln'
    Set-Content -Path $solution -Value 'Microsoft Visual Studio Solution File, Format Version 12.00'

    $older = Join-Path $fixture 'Logs/old.log'
    $newer = Join-Path $fixture 'Logs/newest.log'
    Set-Content -Path $older -Value 'older error'
    Set-Content -Path $newer -Value 'newest error'
    (Get-Item $older).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-30)
    (Get-Item $newer).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-2)

    $actual = Find-Latest $solution
    if ($actual -ne $newer) { throw "Expected '$newer' but got '$actual'" }

    # It must find the directly mapped file too (no File.GetAttributes on relative path).
    $csvFixtureDir = Join-Path $fixture 'CsvFixture'
    New-Item -ItemType Directory -Path (Join-Path $csvFixtureDir 'logs/live') -Force | Out-Null
    $csvSolution = Join-Path $csvFixtureDir 'DevDeck.CsvFixture.sln'
    $csvLog = Join-Path $csvFixtureDir 'logs/live/error-log.csv'
    Set-Content -Path $csvSolution -Value 'Microsoft Visual Studio Solution File, Format Version 12.00'
    Set-Content -Path $csvLog -Value 'log row'
    $actual = Find-Latest $csvSolution
    if ($actual -ne $csvLog) { throw "Expected '$csvLog' but got '$actual'" }

    Remove-Item $newer, $older -Force
    $actual = Find-Latest $solution
    if ($actual -ne 'Not Found') { throw "Expected Not Found but got '$actual'" }
    Write-Host 'PASS: folder timestamps, direct-file mapping and missing-log behavior.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
