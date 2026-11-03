# Creates disposable Git repositories and runs the SAME GitSyncService used by the device.
# Requires .NET 8 SDK and Git for Windows. Does not touch any existing repositories.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = Join-Path ([System.IO.Path]::GetTempPath()) ('DevDeckSmoke-' + [guid]::NewGuid().ToString('N'))
$origin = Join-Path $test 'origin.git'
$seed = Join-Path $test 'seed'
$work = Join-Path $test 'work'
New-Item -ItemType Directory -Force $test | Out-Null
function Git([string]$Directory, [string[]]$Arguments) {
  & git -C $Directory @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
}
try {
  & git init -q --bare $origin
  if ($LASTEXITCODE -ne 0) { throw 'Could not create bare repo' }
  & git init -q -b main $seed
  if ($LASTEXITCODE -ne 0) { throw 'Could not initialise seed' }
  Git $seed @('config','user.name','Smoke Tester')
  Git $seed @('config','user.email','tester@example.invalid')
  Set-Content (Join-Path $seed 'README.md') 'initial'
  Git $seed @('add','--all')
  Git $seed @('commit','-qm','Initial')
  Git $seed @('remote','add','origin',$origin)
  Git $seed @('push','-q','-u','origin','main')
  Git $origin @('symbolic-ref','HEAD','refs/heads/main')

  & git clone -q $origin $work
  if ($LASTEXITCODE -ne 0) { throw 'Clone failed' }
  Git $work @('config','user.name','Smoke Tester')
  Git $work @('config','user.email','tester@example.invalid')
  Git $work @('switch','-q','-c','feature/my-work')
  $localMainBefore = (& git -C $work rev-parse main).Trim()

  # Make a new commit on remote main after our feature branch was created.
  Set-Content (Join-Path $seed 'upstream.txt') 'upstream change'
  Git $seed @('add','--all')
  Git $seed @('commit','-qm','Remote main advanced')
  Git $seed @('push','-q','origin','main')

  # Simulate uncommitted local user changes including an untracked file.
  Set-Content (Join-Path $work 'local.txt') 'local change'
  Push-Location $root
  try {
    & dotnet run --project 'src/chris7197.DevDeck.Cli' -- '--repo' $work
    if ($LASTEXITCODE -ne 0) { throw 'DevDeck CLI returned an error' }
  } finally { Pop-Location }

  $branch = (& git -C $work branch --show-current).Trim()
  if ($branch -ne 'feature/my-work') { throw "Branch unexpectedly changed to $branch" }
  $localMainAfter = (& git -C $work rev-parse main).Trim()
  if ($localMainBefore -ne $localMainAfter) { throw 'Local main was changed' }
  $commit = (& git -C $work log --format=%s --grep='^Pre merge$' -1).Trim()
  if ($commit -ne 'Pre merge') { throw 'Did not create Pre merge commit' }
  if (-not (Test-Path (Join-Path $work 'upstream.txt'))) { throw 'Did not merge remote main' }
  if (-not (Test-Path (Join-Path $work 'local.txt'))) { throw 'Did not commit local file' }
  $dirty = (& git -C $work status --porcelain).Trim()
  if ($dirty) { throw 'Working tree is unexpectedly dirty' }
  Write-Host 'PASS: commit + fetch + merge worked; feature branch retained; local main untouched.'
} finally {
  Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue
}
