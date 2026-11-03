# Disposable end-to-end parser tests. Never opens Visual Studio or a log viewer.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('DevDeckLastError-' + [guid]::NewGuid().ToString('N'))
function Run-Cli([string[]]$Arguments) {
    Push-Location $root
    try {
        $output = & dotnet run --project '.\src\chris7197.DevDeck.Cli' -- @Arguments
        if ($LASTEXITCODE -ne 0) { throw "DevDeck CLI failed: $output" }
        return ($output -join [Environment]::NewLine)
    } finally { Pop-Location }
}
try {
    $solution = Join-Path $fixture 'DevDeck.LogFixture.sln'
    $logs = Join-Path $fixture 'Logs'
    $source = Join-Path $fixture 'src\Worker.cs'
    New-Item $logs -ItemType Directory -Force | Out-Null
    New-Item (Split-Path $source) -ItemType Directory -Force | Out-Null
    Set-Content $solution 'Microsoft Visual Studio Solution File, Format Version 12.00'
    Set-Content $source '// error location fixture'
    $log = Join-Path $logs 'application.log'
    Set-Content $log @(
      '2026-10-12 INFO initialised',
      '2026-10-12 ERROR System.InvalidOperationException: Something failed',
      "   at Demo.Worker.Run() in ${source}:line 23",
      '2026-10-12 INFO normal progress')
    $output = Run-Cli @('--find-last-error', $solution)
    if ($output -notlike '*Something failed*') { throw "Error was not found: $output" }
    if ($output -notlike '*Line: 23*') { throw "Wrong source line: $output" }
    if ($output -notlike "*Source: $source*") { throw "Wrong source file: $output" }

    # CSV logs can have quoted, multiline stack traces (CSV-style direct-file mapping).
    $csvFixtureDir = Join-Path $fixture 'CsvFixture'
    $csvSource = Join-Path $csvFixtureDir 'src\Worker.cs'
    $csv = Join-Path $csvFixtureDir 'logs\live\error-log.csv'
    New-Item (Split-Path $csv) -ItemType Directory -Force | Out-Null
    New-Item (Split-Path $csvSource) -ItemType Directory -Force | Out-Null
    Set-Content $csvSource '// csv stack target'
    $csvSolution = Join-Path $csvFixtureDir 'DevDeck.CsvFixture.sln'
    Set-Content $csvSolution 'Microsoft Visual Studio Solution File, Format Version 12.00'
    $row = '2026-10-12,ERROR,"Operation failed","System.InvalidOperationException",' + '"at Demo.Run() in ' + $csvSource + ':line 37"'
    Set-Content $csv -Value @('timestamp,level,message,exception,stacktrace', '2026-10-12,INFO,Started,,', $row, '2026-10-12,INFO,Done,,')
    $output = Run-Cli @('--find-last-error', $csvSolution)
    if ($output -notlike '*Operation failed*' -or $output -notlike '*Line: 37*') { throw "CSV error not found: $output" }

    Write-Host 'PASS: text error, source line, CSV error and later INFO entries.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
