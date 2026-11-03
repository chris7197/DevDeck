# Safe CLI fixture: verifies shared developer-context resolution and registered actions.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('DevDeckContext-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    $solution = Join-Path $fixture 'DevDeck.LogFixture.sln'
    Set-Content $solution 'Microsoft Visual Studio Solution File, Format Version 12.00'
    Push-Location $root
    try {
        $output = & dotnet run --project '.\src\chris7197.DevDeck.Cli' -- --show-context $solution
        if ($LASTEXITCODE -ne 0) { throw 'Context CLI command failed.' }
        if (-not ($output | Where-Object { $_ -eq "Solution: $solution" })) { throw "Solution path missing: $output" }
        if (-not ($output | Where-Object { $_ -eq 'Solution name: DevDeck.LogFixture' })) { throw "Solution name missing: $output" }

        $actions = & dotnet run --project '.\src\chris7197.DevDeck.Cli' -- --list-actions
        if ($LASTEXITCODE -ne 0) { throw 'Action listing failed.' }
        if (-not ($actions | Where-Object { $_ -like 'com.chris7197.devdeck.syncmain:*' })) { throw 'Git action missing.' }
        if (-not ($actions | Where-Object { $_ -like 'com.chris7197.devdeck.latesterrorlog:*' })) { throw 'Log action missing.' }
    } finally { Pop-Location }
    Write-Host 'PASS: shared context and both registered actions.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
