<#
.SYNOPSIS
    Publishes Strata to artifacts/publish and installs it.

.DESCRIPTION
    The two halves of getting Strata out of a build directory and into a permanent home:

      1. Build the Angular UI and publish the host, complete, into artifacts/publish.
      2. Run 'strata install' from there, which copies it to the program directory and registers
         the logon task.

    They are separate on purpose. The publish needs the repository, the SDK and Node; the install
    needs none of those, only the published folder, so a publish can be copied to another machine
    and installed there. See docs/RUNTIME.md.

.PARAMETER Path
    The program directory. Defaults to %LOCALAPPDATA%\Programs\Strata.

.PARAMETER SelfContained
    Publish the .NET runtime alongside Strata. 105.5 MB rather than 3.8 MB, measured; only worth it
    for a machine with no .NET runtime at all. The shipped default is framework-dependent with
    RollForward=Major. See docs/MEASUREMENTS.md.

.PARAMETER SkipWebUi
    Do not run 'npm run build'. Whatever is already in src/Strata.Web/dist gets published; if that
    is nothing, the install has the API and no UI and says so.

.PARAMETER StageOnly
    Publish, and stop. Nothing is copied and no task is registered.

.PARAMETER Delay
    Seconds between logon and launch. Default 30, 0 for none.

.PARAMETER NoStart
    Install without starting Strata. It will still start at the next logon.

.EXAMPLE
    tools\publish.ps1

.EXAMPLE
    tools\publish.ps1 -SelfContained -Path D:\Tools\Strata
#>
[CmdletBinding()]
param(
    [string] $Path,
    [switch] $SelfContained,
    [switch] $SkipWebUi,
    [switch] $StageOnly,
    [int]    $Delay = 30,
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $repo 'artifacts\publish'
$web     = Join-Path $repo 'src\Strata.Web'

if (-not $SkipWebUi) {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw "npm is not on PATH. Install Node, or pass -SkipWebUi to publish the API alone."
    }

    if (-not (Test-Path (Join-Path $web 'node_modules'))) {
        Write-Host "npm ci in $web..." -ForegroundColor Cyan
        Push-Location $web; try { npm ci } finally { Pop-Location }
    }

    Write-Host "Building the Angular UI..." -ForegroundColor Cyan
    Push-Location $web; try { npm run build } finally { Pop-Location }

    if ($LASTEXITCODE -ne 0) { throw "ng build failed." }
}

# Wholesale, so a stale bundle from a previous publish cannot travel with this one.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

Write-Host "Publishing to $staging..." -ForegroundColor Cyan

# -r win-x64 even framework-dependent: without a RID the publish carries SQLitePCLRaw's native
# library for twenty platforms, 28.5 MB of the 30.6 MB total, none of which this will ever load.
$arguments = @(
    'publish', (Join-Path $repo 'src\Strata.Host'),
    '-c', 'Release',
    '-r', 'win-x64',
    "--self-contained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
    '-o', $staging,
    '--nologo'
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$size = Get-ChildItem $staging -Recurse -File | Measure-Object -Sum Length
Write-Host ("Published {0} files, {1:N1} MB" -f $size.Count, ($size.Sum / 1MB)) -ForegroundColor Green

if ($StageOnly) {
    Write-Host "Staged only. Install it with:  `"$staging\strata.exe`" install"
    return
}

$install = @('install', '--delay', $Delay)
if ($Path)    { $install += @('--path', $Path) }
if ($NoStart) { $install += '--no-start' }

Write-Host ""
& (Join-Path $staging 'strata.exe') @install
exit $LASTEXITCODE
