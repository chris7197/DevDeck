$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/chris7197.DevDeck.Cli/chris7197.DevDeck.Cli.csproj'

Write-Host 'Checking configured application/profile rules...'
dotnet run --project $project -- --test-profile-rules
if ($LASTEXITCODE -ne 0) { throw 'Profile matching test failed.' }

foreach ($profile in @('VisualStudio', 'SqlServer', 'BrowserBi', 'Default')) {
    Write-Host "Checking 15 slots in $profile..."
    $output = @(dotnet run --project $project -- --show-profile $profile)
    if ($LASTEXITCODE -ne 0) { throw "Profile $profile failed to load." }
    if (@($output | Where-Object { $_ -match '^\d{2}: ' }).Count -ne 15) {
        throw "Profile $profile did not render exactly 15 slots."
    }
}
Write-Host 'Profile routing checks passed.'
