# Builds the Windows installer: publishes the app (self-contained, with the
# .NET runtime, so it runs without one installed) to ..\build\publish, then
# compiles WormholeSend.iss with Inno Setup into ..\build.
#
#   .\build.ps1                  # builds the core with cargo first
#   .\build.ps1 -SkipCoreBuild   # uses the wormhole.dll already in target\release

param([switch]$SkipCoreBuild)

$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot
$build = Join-Path $windows 'build'
$publish = Join-Path $build 'publish'

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 is not installed: winget install JRSoftware.InnoSetup'
}

# A clean publish, so no file of an older build ends up in the installer.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$publishArgs = @(
    'publish', (Join-Path $windows 'WormholeSend\WormholeSend.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish
)
if ($SkipCoreBuild) { $publishArgs += '-p:SkipCoreBuild=true' }
dotnet @publishArgs
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The csproj's <Version>, without the commit hash the SDK appends.
$version = (Get-Item (Join-Path $publish 'WormholeSend.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''

& $iscc /Qp "/DAppVersion=$version" (Join-Path $PSScriptRoot 'WormholeSend.iss')
if ($LASTEXITCODE) { throw "Inno Setup failed ($LASTEXITCODE)" }

Get-Item (Join-Path $build "WormholeSend-$version-setup-x64.exe")
