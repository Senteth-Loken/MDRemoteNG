[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'Release Self-Contained')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
$repositoryPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$locatorPath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $locatorPath)) { throw 'Visual Studio 2026 and its MSBuild tools are required.' }
$installationPath = & $locatorPath -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -property installationPath
if (-not $installationPath) { throw 'Visual Studio 2026 MSBuild was not found.' }
$buildExecutable = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
& $buildExecutable (Join-Path $repositoryPath 'mRemoteNG\mRemoteNG.csproj') /restore /t:Build "/p:Configuration=$Configuration" "/p:Platform=$Platform" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "MDRemoteNG build failed with exit code $LASTEXITCODE." }
