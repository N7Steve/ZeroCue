[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-PathInside {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $fullPath = Get-NormalizedFullPath -Path $Path
    $fullParent = (Get-NormalizedFullPath -Path $ParentPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentPrefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    if ($fullPath -ne $fullParent -and -not $fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to touch path outside expected directory. Path: $fullPath Parent: $fullParent"
    }

    return $fullPath
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string[]]$SourceRoots,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $source = $null
    foreach ($root in $SourceRoots) {
        $candidate = Join-Path $root $RelativePath
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $source = $candidate
            break
        }
    }

    if ($null -eq $source) {
        throw "Required portable file is missing: $RelativePath"
    }

    $destination = Join-Path $DestinationRoot $RelativePath
    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

$repoRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "ZeroCue.DataProbe\ZeroCue.DataProbe.csproj"
$runtimeAssetVerifier = Join-Path $PSScriptRoot "verify-runtime-assets.ps1"
$thirdPartyCollector = Join-Path $PSScriptRoot "collect-third-party-notices.ps1"
$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishRoot = Join-Path $artifactsRoot "publish"
$portableName = "ZeroCue-v$Version-$Runtime-portable"
$portableDir = Join-Path $publishRoot $portableName
$rawPublishDir = Join-Path $publishRoot ".raw-$portableName"
$resourcesDir = Join-Path $portableDir "resources"
$zipPath = Join-Path $artifactsRoot "$portableName.zip"

Assert-PathInside -Path $artifactsRoot -ParentPath $repoRoot | Out-Null
Assert-PathInside -Path $publishRoot -ParentPath $repoRoot | Out-Null
Assert-PathInside -Path $portableDir -ParentPath $repoRoot | Out-Null
Assert-PathInside -Path $rawPublishDir -ParentPath $repoRoot | Out-Null
Assert-PathInside -Path $resourcesDir -ParentPath $repoRoot | Out-Null
Assert-PathInside -Path $zipPath -ParentPath $repoRoot | Out-Null

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project file not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $runtimeAssetVerifier -PathType Leaf)) {
    throw "Runtime asset verifier not found: $runtimeAssetVerifier"
}
if (-not (Test-Path -LiteralPath $thirdPartyCollector -PathType Leaf)) {
    throw "Third-party notice collector not found: $thirdPartyCollector"
}

& $runtimeAssetVerifier

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

foreach ($pathToClean in @($portableDir, $rawPublishDir)) {
    if (Test-Path -LiteralPath $pathToClean) {
        $resolvedPath = Assert-PathInside -Path (Resolve-Path -LiteralPath $pathToClean).Path -ParentPath $publishRoot
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

if (Test-Path -LiteralPath $zipPath) {
    $resolvedZipPath = Assert-PathInside -Path (Resolve-Path -LiteralPath $zipPath).Path -ParentPath $artifactsRoot
    Remove-Item -LiteralPath $resolvedZipPath -Force
}

New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
New-Item -ItemType Directory -Force -Path $rawPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $resourcesDir | Out-Null

$selfContained = (-not $FrameworkDependent.IsPresent)
$selfContainedValue = if ($selfContained) { "true" } else { "false" }
$numericVersion = ($Version -split '[-+]')[0]
$assemblyVersion = "$numericVersion.0"

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --output $rawPublishDir `
    /p:Version=$Version `
    /p:AssemblyVersion=$assemblyVersion `
    /p:FileVersion=$assemblyVersion `
    /p:InformationalVersion=$Version `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:UseAppHost=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedExePath = Join-Path $rawPublishDir "ZeroCue.DataProbe.exe"
if (-not (Test-Path -LiteralPath $publishedExePath -PathType Leaf)) {
    throw "Publish completed but expected executable was not found: $publishedExePath"
}

Copy-Item -LiteralPath $publishedExePath -Destination (Join-Path $portableDir "zerocue.exe") -Force

$requiredResourceFiles = @(
    "scuf_mapping.json",
    "libusb-1.0.dll",
    "Assets\wdi-simple.exe",
    "THIRD_PARTY_NOTICES.md",
    "LICENSE",
    "NOTICE"
)

$resourceSourceRoots = @(
    $rawPublishDir,
    (Join-Path $repoRoot "ZeroCue.DataProbe"),
    $repoRoot
)

foreach ($file in $requiredResourceFiles) {
    Copy-RequiredFile -SourceRoots $resourceSourceRoots -DestinationRoot $resourcesDir -RelativePath $file
}

& $thirdPartyCollector -DestinationRoot (Join-Path $resourcesDir "licenses")

$blockedUserDataArtifacts = @(
    (Join-Path $portableDir "appsettings.json"),
    (Join-Path $portableDir "Profiles"),
    (Join-Path $portableDir "logs"),
    (Join-Path $resourcesDir "appsettings.json"),
    (Join-Path $resourcesDir "Profiles"),
    (Join-Path $resourcesDir "logs")
)

foreach ($artifact in $blockedUserDataArtifacts) {
    if (Test-Path -LiteralPath $artifact) {
        $resolvedArtifact = Assert-PathInside -Path (Resolve-Path -LiteralPath $artifact).Path -ParentPath $portableDir
        Remove-Item -LiteralPath $resolvedArtifact -Recurse -Force
    }
}

$rootFiles = Get-ChildItem -LiteralPath $portableDir -File
$unexpectedRootFiles = $rootFiles | Where-Object { $_.Name -ne "zerocue.exe" }
if ($unexpectedRootFiles) {
    throw "Portable root must contain only zerocue.exe as a file. Unexpected root files: $($unexpectedRootFiles.Name -join ', ')"
}

$finalRequiredFiles = @(
    (Join-Path $portableDir "zerocue.exe"),
    (Join-Path $resourcesDir "scuf_mapping.json"),
    (Join-Path $resourcesDir "libusb-1.0.dll"),
    (Join-Path $resourcesDir "Assets\wdi-simple.exe"),
    (Join-Path $resourcesDir "THIRD_PARTY_NOTICES.md"),
    (Join-Path $resourcesDir "LICENSE"),
    (Join-Path $resourcesDir "NOTICE"),
    (Join-Path $resourcesDir "licenses\managed-dependencies.md"),
    (Join-Path $resourcesDir "licenses\MIT.txt"),
    (Join-Path $resourcesDir "licenses\libusb-LGPL-2.1.txt"),
    (Join-Path $resourcesDir "licenses\libwdi-LGPL-3.0.txt")
)

foreach ($file in $finalRequiredFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Final portable file is missing: $file"
    }
}

foreach ($artifact in $blockedUserDataArtifacts) {
    if (Test-Path -LiteralPath $artifact) {
        throw "Portable package still contains user data artifact: $artifact"
    }
}

Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

$zipItem = Get-Item -LiteralPath $zipPath
Write-Host "Portable folder: $portableDir"
Write-Host "Portable zip: $($zipItem.FullName)"
Write-Host "Zip size: $([Math]::Round($zipItem.Length / 1MB, 2)) MB"
