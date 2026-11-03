$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'tests/SqlFormatting.SelfTest/SqlFormatting.SelfTest.csproj'
dotnet run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'SQL formatter self-tests failed' }
