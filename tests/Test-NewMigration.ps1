# Disposable migration generator test (does not invoke Visual Studio or run migrations).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('DevDeckMigration-' + [guid]::NewGuid().ToString('N'))
function Run-Cli([string[]]$Arguments) {
    Push-Location $root
    try {
        $output = & dotnet run --project '.\src\chris7197.DevDeck.Cli' -- @Arguments
        if ($LASTEXITCODE -ne 0) { throw "DevDeck CLI failed: $output" }
        return ($output -join [Environment]::NewLine)
    } finally { Pop-Location }
}
try {
    $projectDir = Join-Path $fixture 'Migrator'
    New-Item $projectDir -ItemType Directory -Force | Out-Null
    $csproj = Join-Path $projectDir 'Sample.DBMigrator.csproj'
    Set-Content $csproj '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>Demo.Migrations</RootNamespace></PropertyGroup></Project>'
    $solution = Join-Path $fixture 'Fixture.sln'
    Set-Content $solution @(
        'Microsoft Visual Studio Solution File, Format Version 12.00',
        'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Migrator", "Migrator\Sample.DBMigrator.csproj", "{DB3DF8A0-EDAA-4BA8-B425-0BBEDB210000}"',
        'EndProject')

    $preview = Run-Cli @('--preview-migration', $solution)
    if ($preview -notlike '*Namespace: Demo.Migrations._*') { throw "Bad namespace: $preview" }
    if ($preview -notlike '*Migrator.cs*') { throw "Bad migration path: $preview" }
    if (Test-Path (Join-Path $projectDir '_*')) { throw 'Preview created files.' }

    $created = Run-Cli @('--create-migration-no-open', $solution)
    $path = ($created -split "`n" | Where-Object { $_ -like 'Created: *' } | Select-Object -Last 1).Trim().Substring('Created: '.Length)
    if (-not (Test-Path $path)) { throw "Not created: $path" }
    $contents = Get-Content $path -Raw
    if ($contents -notmatch '\[Migration\(\d{12}\)\]' -or
        $contents -notmatch 'public override void Up\(' -or
        $contents -notmatch 'public override void Down\(') { throw "Invalid migration: $contents" }

    $second = Run-Cli @('--create-migration-no-open', $solution)
    $secondPath = ($second -split "`n" | Where-Object { $_ -like 'Created: *' } | Select-Object -Last 1).Trim().Substring('Created: '.Length)
    if ($secondPath -eq $path) { throw 'Second generation overwrote first file.' }
    if ((Get-Content $path -Raw) -ne $contents) { throw 'Original migration was modified.' }
    Write-Host 'PASS: preview, namespace, generated class, and collision handling.'

    # Legacy MSBuild 2003 XML namespace, explicit Compile files, and conditions.
    $legacyDir = Join-Path $fixture 'LegacyMigrator'
    New-Item $legacyDir -ItemType Directory -Force | Out-Null
    $legacyProject = Join-Path $legacyDir 'Legacy.DBMigrator.csproj'
    $legacyXml = @'
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><RootNamespace>Legacy.Migrations</RootNamespace></PropertyGroup>
  <ItemGroup Condition=" '$(Configuration)' == 'Debug' ">
    <Compile Include="DebugOnly.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Compile Include="Properties\AssemblyInfo.cs" />
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
'@
    [System.IO.File]::WriteAllText($legacyProject, $legacyXml.TrimStart(), (New-Object System.Text.UTF8Encoding($false)))
    $legacySolution = Join-Path $fixture 'LegacyFixture.sln'
    Set-Content $legacySolution @(
        'Microsoft Visual Studio Solution File, Format Version 12.00',
        'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Migrator", "LegacyMigrator\Legacy.DBMigrator.csproj", "{DB3DF8A0-EDAA-4BA8-B425-0BBEDB220000}"',
        'EndProject')

    $legacyFirst = Run-Cli @('--create-migration-no-open', $legacySolution)
    $legacyFirstPath = ($legacyFirst -split "`n" | Where-Object { $_ -like 'Created: *' } | Select-Object -Last 1).Trim().Substring('Created: '.Length)
    if (-not (Test-Path $legacyFirstPath)) { throw 'Legacy migration not generated.' }
    if ($legacyFirst -notlike '*Project inclusion: ProjectFileUpdated*') { throw "Legacy project not updated: $legacyFirst" }
    [xml]$afterFirst = Get-Content $legacyProject -Raw
    $firstRelative = $legacyFirstPath.Substring($legacyDir.Length).TrimStart('\', '/').Replace('/', '\')
    $compileItems = @($afterFirst.SelectNodes('//*[local-name()="Compile"]'))
    if (@($compileItems | Where-Object { $_.Include -eq $firstRelative }).Count -ne 1) {
        throw 'Generated migration not included exactly once in the legacy csproj.'
    }
    if (@($compileItems | Where-Object { $_.Include -eq 'Properties\AssemblyInfo.cs' }).Count -ne 1) {
        throw 'Existing Compile entry was damaged.'
    }
    $unconditional = @($afterFirst.SelectNodes('//*[local-name()="ItemGroup" and not(@Condition)]/*[local-name()="Compile"]'))
    if (@($unconditional | Where-Object { $_.Include -eq $firstRelative }).Count -ne 1) {
        throw 'Migration was accidentally added to a conditional ItemGroup.'
    }

    $legacySecond = Run-Cli @('--create-migration-no-open', $legacySolution)
    $legacySecondPath = ($legacySecond -split "`n" | Where-Object { $_ -like 'Created: *' } | Select-Object -Last 1).Trim().Substring('Created: '.Length)
    if ($legacySecondPath -eq $legacyFirstPath) { throw 'Second legacy migration overwrote the first.' }
    [xml]$afterSecond = Get-Content $legacyProject -Raw
    $legacySecondRelative = $legacySecondPath.Substring($legacyDir.Length).TrimStart('\', '/').Replace('/', '\')
    foreach ($rel in @($firstRelative, $legacySecondRelative)) {
        if (@($afterSecond.SelectNodes('//*[local-name()="Compile"]') | Where-Object { $_.Include -eq $rel }).Count -ne 1) {
            throw "Legacy migration has duplicate/missing Compile entry: $rel"
        }
    }

    # Explicit wildcard Compiles should not receive a redundant single-file entry.
    $wildDir = Join-Path $fixture 'WildcardMigrator'
    New-Item $wildDir -ItemType Directory -Force | Out-Null
    $wildProject = Join-Path $wildDir 'Wildcard.DBMigrator.csproj'
    $wildXml = '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup><Compile Include="**\*.cs" /></ItemGroup></Project>'
    [System.IO.File]::WriteAllText($wildProject, $wildXml)
    $wildSolution = Join-Path $fixture 'WildcardFixture.sln'
    Set-Content $wildSolution @(
        'Microsoft Visual Studio Solution File, Format Version 12.00',
        'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Migrator", "WildcardMigrator\Wildcard.DBMigrator.csproj", "{DB3DF8A0-EDAA-4BA8-B425-0BBEDB230000}"',
        'EndProject')
    $wildResult = Run-Cli @('--create-migration-no-open', $wildSolution)
    if ($wildResult -notlike '*Project inclusion: AlreadyIncluded*') { throw "Wildcard inclusion not detected: $wildResult" }
    if ((Get-Content $wildProject -Raw) -ne $wildXml) { throw 'Project with wildcard was modified.' }

    # Legacy projects with no Compile entries receive a new unconditional ItemGroup.
    $emptyDir = Join-Path $fixture 'EmptyMigrator'
    New-Item $emptyDir -ItemType Directory -Force | Out-Null
    $emptyProject = Join-Path $emptyDir 'Empty.DBMigrator.csproj'
    [System.IO.File]::WriteAllText($emptyProject, '<Project><PropertyGroup><RootNamespace>Empty.Migrations</RootNamespace></PropertyGroup><Import Project="Microsoft.CSharp.targets" /></Project>')
    $emptySolution = Join-Path $fixture 'EmptyFixture.sln'
    Set-Content $emptySolution @(
        'Microsoft Visual Studio Solution File, Format Version 12.00',
        'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Migrator", "EmptyMigrator\Empty.DBMigrator.csproj", "{DB3DF8A0-EDAA-4BA8-B425-0BBEDB240000}"',
        'EndProject')
    $emptyResult = Run-Cli @('--create-migration-no-open', $emptySolution)
    if ($emptyResult -notlike '*Project inclusion: ProjectFileUpdated*') { throw "Empty legacy project not updated: $emptyResult" }
    [xml]$afterEmpty = Get-Content $emptyProject -Raw
    if (@($afterEmpty.SelectNodes('//*[local-name()="Compile"]')).Count -ne 1) { throw 'Compile item was not added to empty project.' }

    # SDK projects must not be edited. The generated file is automatically included.
    if ((Get-Content $csproj -Raw) -notlike '*<Project Sdk="Microsoft.NET.Sdk">*') {
        throw 'SDK project was unexpectedly rewritten.'
    }
    Write-Host 'PASS: legacy Compile inclusion, XML namespace, conditions, wildcard, no Compile group, SDK unchanged.'

} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
