[CmdletBinding()]
param(
    [string]$Package = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\LanView-0.1.1-win-x64.zip'),
    [string]$CompilerPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'work\installer-tools\InnoSetup-6.7.3\ISCC.exe'),
    [Parameter(Mandatory)][string]$MoonlightArchive,
    [string]$MoonlightSourceArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Build the Windows installer with PowerShell 7 on Windows.' }
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$compilerVersion = '6.7.3'
$compilerHash = '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7'
if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne $compilerHash) { throw 'Use ISCC.exe from the pinned official Inno Setup 6.7.3 installer.' }

# Validate the complete payload before extracting or compiling. The input ZIP
# comes from build.ps1; the installer consumes its matching source snapshot.
$packageCheck = @{ Package = $packagePath; MoonlightArchive = $MoonlightArchive }
if ($MoonlightSourceArchive) { $packageCheck.MoonlightSourceArchive = $MoonlightSourceArchive }
& (Join-Path $PSScriptRoot 'test-package.ps1') @packageCheck

$buildRoot = Join-Path $repository ('work\installer-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $buildRoot 'bundle'
$output = Join-Path $buildRoot 'output'
New-Item -ItemType Directory -Path $bundle, $output -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $bundle)
$manifest = Get-Content -LiteralPath (Join-Path $bundle 'bundle.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Invalid package version.' }
$installerScript = Join-Path $bundle 'source\LanView\packaging\LanView.iss'
if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) { throw 'Rebuild the Windows ZIP so it includes the installer source.' }
& $compiler "/DBundleDir=$bundle" "/DProductVersion=$version" "/DSetupOutputDir=$output" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$name = "LanView-$version-Setup-win-x64.exe"
$stagedInstaller = Join-Path $output $name
if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) { throw 'Compiled installer is missing.' }
$artifacts = Join-Path $repository 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$finalInstaller = Join-Path $artifacts $name
Copy-Item -LiteralPath $stagedInstaller -Destination $finalInstaller -Force
$hash = (Get-FileHash -LiteralPath $finalInstaller -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($finalInstaller + '.sha256'), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Installer = $finalInstaller; Bytes = (Get-Item -LiteralPath $finalInstaller).Length
    SHA256 = $hash; PackageSHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    CompilerVersion = $compilerVersion; StagedExecutable = (Join-Path $bundle 'LanView.exe')
} | Format-List
