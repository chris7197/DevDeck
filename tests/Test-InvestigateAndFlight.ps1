$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet run --project .\src\chris7197.DevDeck.Cli -- --selftest-investigation
    if ($LASTEXITCODE -ne 0) { throw 'Investigation classifier/registration self-test failed.' }
    $manifest = Get-Content .\plugin\manifest.json -Raw | ConvertFrom-Json
    $ids = @($manifest.Actions | ForEach-Object { $_.UUID })
    foreach ($name in @('investigate', 'dbflight')) {
        if ($ids -notcontains "com.chris7197.devdeck.$name") { throw "Missing SOOMFON action: $name" }
    }
    $settings = Get-Content .\plugin\settings.json -Raw | ConvertFrom-Json
    if ($null -eq $settings.DbFlightRecorderTargets) { throw 'Missing DB target configuration.' }
    # Small disposable solution/source search smoke test. Never contacts SQL or any service.
    $fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('DevDeck-Investigate-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    try {
        $solution = Join-Path $fixture 'Fixture.sln'
        Set-Content $solution ''
        Set-Content (Join-Path $fixture 'Queries.sql') 'SELECT * FROM dbo.TestOrder;'
        $out = @(dotnet run --project .\src\chris7197.DevDeck.Cli -- --investigate-text 'dbo.TestOrder' $solution)
        if ($LASTEXITCODE -ne 0) { throw 'CLI investigate-text failed' }
        $reportLine = @($out | Where-Object { $_ -like 'Report: *' }) | Select-Object -First 1
        if (-not $reportLine) { throw 'No investigation report path returned' }
        $report = $reportLine.Substring(8)
        if ((Get-Content $report -Raw) -notmatch 'Queries.sql') { throw 'Expected source match missing from report' }
    }
    finally { Remove-Item -LiteralPath $fixture -Force -Recurse -ErrorAction SilentlyContinue }
    Write-Host 'Offline investigation/flight wiring: PASS' -ForegroundColor Green
    Write-Host 'This test does not connect to SQL Server or access the clipboard.'
}
finally { Pop-Location }
