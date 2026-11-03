# Run as: .\scripts\Format-CSharp.ps1 -Path 'C:\repo\Class.cs' [-Check]
# Also supports explicitly targeting a directory, but DevDeck only supplies its active file.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [switch]$Check,
    [string]$ConfigPath = ''
)
$ErrorActionPreference = 'Stop'

try {
    $target = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $target)) { throw "Target not found: $target" }
    if ((Test-Path -LiteralPath $target -PathType Leaf) -and
        [IO.Path]::GetExtension($target) -ne '.cs') {
        throw 'Only .cs files can be passed to this formatter.'
    }

    # Prefer a repo-pinned local CSharpier tool over a globally installed one.
    $directory = if (Test-Path -LiteralPath $target -PathType Container) { $target } else { Split-Path $target -Parent }
    $current = $directory
    $localTool = $false
    $hasProjectConfig = $false
    while ($current) {
        $manifest = Join-Path $current '.config/dotnet-tools.json'
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            try {
                $data = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
                if ($data.tools -and ($data.tools.PSObject.Properties.Name -contains 'csharpier')) { $localTool = $true }
            } catch { throw "Invalid dotnet tool manifest: $manifest. $_" }
        }
        if (@('.csharpierrc', '.csharpierrc.json', '.csharpierrc.yaml', '.editorconfig') |
                Where-Object { Test-Path -LiteralPath (Join-Path $current $_) -PathType Leaf }) {
            $hasProjectConfig = $true
        }
        $parent = Split-Path $current -Parent
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }

    if ($localTool) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'dotnet SDK is required for the repository-local CSharpier tool.'
        }
        $exe = 'dotnet'
        $arguments = @('csharpier')
    }
    elseif (Get-Command csharpier -ErrorAction SilentlyContinue) {
        $exe = 'csharpier'
        $arguments = @()
    }
    else {
        throw 'CSharpier is not installed. Run dotnet tool install -g csharpier, or install it in a local dotnet tool manifest.'
    }

    $arguments += $(if ($Check) { 'check' } else { 'format' })
    $arguments += $target
    if ($ConfigPath) {
        if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "CSharpier config not found: $ConfigPath" }
        $arguments += @('--config-path', [IO.Path]::GetFullPath($ConfigPath))
    }
    elseif (-not $hasProjectConfig) {
        # The user's preferred 140-column layout without adding files to their repository.
        $arguments += @('--config-path', (Join-Path $PSScriptRoot 'csharpier-defaults.json'))
    }

    Write-Host "CSharpier: $target"
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
