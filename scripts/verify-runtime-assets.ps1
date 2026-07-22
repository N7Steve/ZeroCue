[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectRoot = Join-Path $repoRoot "ZeroCue.DataProbe"

$forbiddenRepositoryArtifacts = Get-ChildItem -LiteralPath $repoRoot -File | Where-Object {
    $_.Extension -in @(".pcap", ".pcapng", ".etl", ".dmp") -or
    $_.Name -match '(?i)(capture|trace|debug|icue).*[.]txt$'
}

if ($forbiddenRepositoryArtifacts) {
    throw "Diagnostic artifacts must not be stored in the repository root: $($forbiddenRepositoryArtifacts.Name -join ', ')"
}

function Get-RepoRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $repoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the repository: $fullPath"
    }

    return $fullPath.Substring($rootPrefix.Length)
}

$expectedFiles = @{
    "libusb-1.0.dll" = "7CBF37E76DAE9C840C7E8DBF7348EE8897DCC86C8BA45E46ADA60B89411569F7"
    "Assets\wdi-simple.exe" = "8C22510BD4431152E7DF787D7135A00875AF7BB3AFC090D2AB72E859EFB25E33"
}

foreach ($relativePath in $expectedFiles.Keys) {
    $fullPath = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required runtime asset is missing: $relativePath"
    }

    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedFiles[$relativePath]) {
        throw "Runtime asset hash mismatch: $relativePath`nExpected: $($expectedFiles[$relativePath])`nActual:   $actualHash"
    }
}

$assetsRoot = Join-Path $projectRoot "Assets"
$forbiddenFiles = Get-ChildItem -LiteralPath $assetsRoot -Recurse -File | Where-Object {
    $_.Extension -in @(".svg", ".ps1", ".txt") -or
    $_.FullName -match '[\\/](P4Gamepad|P5Gamepad)[\\/]' -or
    ($_.FullName -match '[\\/]XGamepad[\\/]' -and $_.Name -match 'Sprite\.png$')
}

if ($forbiddenFiles) {
    $relativeNames = $forbiddenFiles | ForEach-Object {
        Get-RepoRelativePath -Path $_.FullName
    }
    throw "Development-only assets are present:`n$($relativeNames -join "`n")"
}

$switchAssets = Get-ChildItem -LiteralPath (Join-Path $assetsRoot "gamepad-icons\SGamepad") -Recurse -File
$unexpectedSwitchAssets = $switchAssets | Where-Object {
    (Get-RepoRelativePath -Path $_.FullName) -ne "ZeroCue.DataProbe\Assets\gamepad-icons\SGamepad\Default\T_S_Home.png"
}

if ($unexpectedSwitchAssets) {
    throw "Unused SGamepad assets are present: $($unexpectedSwitchAssets.FullName -join ', ')"
}

Write-Host "Bundled runtime assets and embedded-resource boundaries are valid."
