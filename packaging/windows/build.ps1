param(
  [string]$Dotnet = 'dotnet',
  [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\dist\win\DUSMP-Launcher-Setup.exe')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishFlags = @(
  '-c', 'Release', '--self-contained', 'true', '-r', 'win-x64',
  '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:DebugType=None', '-p:DebugSymbols=false'
)

& $Dotnet publish (Join-Path $repoRoot 'src\DusmpLauncher') @publishFlags -o (Join-Path $repoRoot 'dist\win\launcher')
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed with exit code $LASTEXITCODE." }

& $Dotnet publish (Join-Path $repoRoot 'src\DusmpUpdater') @publishFlags -o (Join-Path $repoRoot 'dist\win\updater')
if ($LASTEXITCODE -ne 0) { throw "Updater publish failed with exit code $LASTEXITCODE." }

& (Join-Path $PSScriptRoot 'build-installer.ps1') -OutputPath $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Installer packaging failed with exit code $LASTEXITCODE." }
