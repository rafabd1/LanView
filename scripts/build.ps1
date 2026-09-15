[CmdletBinding()]
param(
    [string]$MoonlightArchive,
    [string]$MoonlightSourceArchive,
    [string]$DownloadCache = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cache\LanView\downloads'),
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $repository 'src\LanView.Windows\LanView.Windows.csproj'
$artifacts = Join-Path $repository 'artifacts'
$buildRoot = Join-Path $repository ('work\package-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $buildRoot 'bundle'
$runtimeVersion = '10.0.11'
$moonlightVersion = '6.1.0'
$moonlightName = 'MoonlightPortable-x64-6.1.0.zip'
$sourceName = 'MoonlightSrc-6.1.0.tar.gz'
$moonlightHash = '95F4D0853A31C7FCED4B6D233DDF55EE41720963F2E2620A9CB49A21D112AED1'
$sourceHash = '696CC470A62E2F2E9B77739D400B389E7578C9510383C08614007C92BE49D5B0'
$releaseUrl = 'https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0'

function Get-VerifiedArchive {
    param([string]$ProvidedPath, [string]$Name, [string]$ExpectedHash, [long]$ExpectedSize)
    $candidate = if ($ProvidedPath) { (Resolve-Path -LiteralPath $ProvidedPath).Path } else { Join-Path ([IO.Path]::GetFullPath($DownloadCache)) $Name }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        if ($ProvidedPath -or $Offline) { throw "Archive not available: $Name" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $candidate) -Force | Out-Null
        $partial = $candidate + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
        Invoke-WebRequest -Uri "$releaseUrl/$Name" -OutFile $partial -ProgressAction SilentlyContinue
        if ((Get-Item -LiteralPath $partial).Length -ne $ExpectedSize -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $ExpectedHash) {
            throw "The downloaded $Name does not match the pinned official archive."
        }
        Move-Item -LiteralPath $partial -Destination $candidate
    }
    if ((Get-Item -LiteralPath $candidate).Length -ne $ExpectedSize -or (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $ExpectedHash) {
        throw "$Name has the wrong size or SHA256. Only the unmodified official archive is accepted."
    }
    return $candidate
}

function Get-SourceFiles {
    param([string]$Directory)
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin @('.git', 'bin', 'obj', 'build', '.build', 'artifacts', 'work', 'node_modules', '.vros', '__pycache__')) { Get-SourceFiles -Directory $entry.FullName }
        } elseif ($entry.Extension -in @('.cs', '.csproj', '.props', '.targets', '.resx', '.ico', '.manifest', '.c', '.h', '.cpp', '.py', '.ps1', '.iss', '.sh', '.service', '.socket', '.timer', '.md', '.example') -or $entry.Name -in @('CMakeLists.txt', 'requirements.txt', 'Makefile', 'LICENSE', 'LICENSE.txt', 'COPYING')) {
            $entry
        } elseif ($entry.FullName -match '[\\/]host[\\/]clipboard[\\/]protocols[\\/][^\\/]+\.xml$') {
            $entry
        } elseif ($entry.FullName -match '[\\/]src[\\/]LanView\.Windows[\\/]Assets[\\/]LanView\.png$') {
            $entry
        } elseif (-not $entry.Extension -and $entry.Name -like 'lanview-*') {
            # Extensionless host entry points must be scripts, not installed binaries or state.
            if ((Get-Content -LiteralPath $entry.FullName -TotalCount 1) -match '^#!') { $entry }
        }
    }
}

function Copy-SourceDirectory {
    param([string]$Source, [string]$Destination)
    foreach ($file in Get-SourceFiles -Directory $Source) {
        $target = Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Copy-RuntimeNotices {
    param([string]$AssetsPath)
    $assetsDocument = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packageDirectory = $null
        foreach ($packageRoot in $assetsDocument.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $packageRoot "$package\$runtimeVersion"
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        if (-not $packageDirectory) { throw "Runtime license directory not found for $package $runtimeVersion." }
        $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })
        if ($notices.Count -eq 0) { throw "Runtime license not found for $package." }
        $target = Join-Path $bundle "licenses\$package"
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target }
    }
}

if (-not $IsWindows) { throw 'Build this Windows package on Windows with PowerShell 7 and the .NET 10 SDK.' }
Get-Command dotnet, tar -ErrorAction Stop | Out-Null
$clientArchive = Get-VerifiedArchive $MoonlightArchive $moonlightName $moonlightHash 29502440
$sourceArchive = Get-VerifiedArchive $MoonlightSourceArchive $sourceName $sourceHash 85936443
New-Item -ItemType Directory -Path $bundle, $artifacts -Force | Out-Null
$lanviewSource = Join-Path $buildRoot 'source\LanView'
foreach ($directory in @('src', 'host', 'tests', 'scripts', 'packaging')) { Copy-SourceDirectory -Source (Join-Path $repository $directory) -Destination (Join-Path $lanviewSource $directory) }
foreach ($name in @('README.md', 'THIRD_PARTY.md')) { Copy-Item -LiteralPath (Join-Path $repository $name) -Destination (Join-Path $lanviewSource $name) }
$snapshotProject = Join-Path $lanviewSource 'src\LanView.Windows\LanView.Windows.csproj'
$restoreArguments = @('restore', $snapshotProject, '-r', 'win-x64', '--artifacts-path', (Join-Path $buildRoot 'dotnet'), '-p:SelfContained=true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', "-p:RuntimeFrameworkVersion=$runtimeVersion", '--nologo')
if ($Offline) {
    $emptyFeed = Join-Path $buildRoot 'empty-feed'
    New-Item -ItemType Directory -Path $emptyFeed | Out-Null
    $restoreArguments += @('--source', $emptyFeed, '-p:NuGetAudit=false')
}
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Offline builds require the .NET 10.0.11 Windows runtime packs in the NuGet cache.' }
& dotnet publish $snapshotProject -c Release -r win-x64 --self-contained true --no-restore --artifacts-path (Join-Path $buildRoot 'dotnet') -o $bundle -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:RuntimeFrameworkVersion=$runtimeVersion" -p:DebugType=none -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $bundle 'LanView.exe') -PathType Leaf)) { throw 'Published executable is missing.' }
$iconDirectory = Join-Path $bundle 'assets'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $lanviewSource 'src\LanView.Windows\Assets\LanView.ico') -Destination (Join-Path $iconDirectory 'LanView.ico')
$moonlightDirectory = Join-Path $bundle 'tools\Moonlight'
[IO.Compression.ZipFile]::ExtractToDirectory($clientArchive, $moonlightDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $moonlightDirectory 'Moonlight.exe') -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $moonlightDirectory 'portable.dat') -PathType Leaf)) { throw 'The official Moonlight archive has an unexpected layout.' }
$moonlightNotices = Join-Path $bundle 'licenses\moonlight'
New-Item -ItemType Directory -Path $moonlightNotices -Force | Out-Null
$noticeEntries = @('LICENSE', 'app/SDL_GameControllerDB/LICENSE', 'h264bitstream/h264bitstream/LICENSE', 'moonlight-common-c/moonlight-common-c/LICENSE.txt', 'moonlight-common-c/moonlight-common-c/enet/LICENSE', 'qmdnsengine/qmdnsengine/LICENSE.txt', 'soundio/libsoundio/LICENSE')
& tar -xzf $sourceArchive -C $moonlightNotices -- @noticeEntries
if ($LASTEXITCODE -ne 0) { throw 'Could not extract upstream license texts.' }
Copy-RuntimeNotices -AssetsPath (Join-Path $buildRoot 'dotnet\obj\LanView.Windows\project.assets.json')
Copy-SourceDirectory -Source $lanviewSource -Destination (Join-Path $bundle 'source\LanView')
Copy-SourceDirectory -Source (Join-Path $lanviewSource 'host') -Destination (Join-Path $bundle 'host')
foreach ($name in @('README.md', 'SOURCE.md')) { Copy-Item -LiteralPath (Join-Path $lanviewSource "packaging\$name") -Destination (Join-Path $bundle $name) }
Copy-Item -LiteralPath (Join-Path $lanviewSource 'THIRD_PARTY.md') -Destination (Join-Path $bundle 'THIRD_PARTY.md')
$unexpectedState = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { $_.Name -match '^(profile|viewer-window|gpu-clock|credentials?|sunshine_state|identity)\.(json|xml|ini)$' -or $_.Extension -in @('.pem', '.key', '.pfx', '.p12', '.log', '.dmp') })
if ($unexpectedState.Count -gt 0) { throw 'Local state or credential files were found in the staged package.' }
[xml]$projectDocument = Get-Content -LiteralPath $snapshotProject -Raw
$version = [string]$projectDocument.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Project version is invalid for packaging.' }
$manifest = [ordered]@{
    product = 'LanView'; version = $version; platform = 'win-x64'; selfContained = $true
    dotnetRuntime = $runtimeVersion; trimmed = $false; containsLocalProfile = $false
    moonlight = [ordered]@{ version = $moonlightVersion; archive = $moonlightName; sha256 = $moonlightHash; url = "$releaseUrl/$moonlightName" }
    moonlightSource = [ordered]@{ archive = $sourceName; sha256 = $sourceHash; url = "$releaseUrl/$sourceName" }
}
[IO.File]::WriteAllText((Join-Path $bundle 'bundle.json'), ($manifest | ConvertTo-Json -Depth 4) + "`n", [Text.UTF8Encoding]::new($false))
$inventory = foreach ($file in Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash, [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\', '/') }
[IO.File]::WriteAllLines((Join-Path $bundle 'checksums.sha256'), $inventory, [Text.UTF8Encoding]::new($false))
$zipName = "LanView-$version-win-x64.zip"
$stagedZip = Join-Path $buildRoot $zipName
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$finalZip = Join-Path $artifacts $zipName
Copy-Item -LiteralPath $stagedZip -Destination $finalZip -Force
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $artifacts $sourceName) -Force
$zipHash = (Get-FileHash -LiteralPath $finalZip -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $artifacts ($zipName + '.sha256')), "$zipHash  $zipName`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $artifacts ($sourceName + '.sha256')), "$sourceHash  $sourceName`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Package = $finalZip; Bytes = (Get-Item -LiteralPath $finalZip).Length; SHA256 = $zipHash; SourceArchive = (Join-Path $artifacts $sourceName); StagedExecutable = (Join-Path $bundle 'LanView.exe') } | Format-List
