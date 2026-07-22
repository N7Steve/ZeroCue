[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectRoot = Join-Path $repoRoot "ZeroCue.DataProbe"
$lockPath = Join-Path $projectRoot "packages.lock.json"
$assetsPath = Join-Path $projectRoot "obj\project.assets.json"
$destination = [System.IO.Path]::GetFullPath($DestinationRoot)
$managedDestination = Join-Path $destination "managed"
$dotnetDestination = Join-Path $destination "dotnet-runtime"

function Get-XmlChildText {
    param(
        [Parameter(Mandatory = $true)]$Parent,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $node = $Parent.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node) { return "" }
    return $node.InnerText.Trim()
}

function Get-SafeSegment {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [regex]::Replace($Value, '[^0-9A-Za-z._-]', '_')
}

function Escape-MarkdownCell {
    param([AllowEmptyString()][string]$Value)
    return (($Value -replace '\|', '\|') -replace '[\r\n]+', ' ').Trim()
}

function Get-LicenseFiles {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [AllowEmptyString()][string]$DeclaredFile
    )

    $found = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($DeclaredFile)) {
        $declaredPath = Join-Path $PackageRoot $DeclaredFile
        if (-not (Test-Path -LiteralPath $declaredPath -PathType Leaf)) {
            throw "Declared package license file is missing: $declaredPath"
        }
        $found.Add((Resolve-Path -LiteralPath $declaredPath).Path)
    }

    Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Where-Object {
        $_.Name -match '^(license|copying|notice|third[-_. ]?party)'
    } | ForEach-Object {
        $resolved = $_.FullName
        if (-not $found.Contains($resolved)) { $found.Add($resolved) }
    }

    return $found.ToArray()
}

function Copy-LicenseFiles {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Files,
        [Parameter(Mandatory = $true)][string]$PackageDestination
    )

    if ($Files.Count -eq 0) { return @() }
    New-Item -ItemType Directory -Force -Path $PackageDestination | Out-Null
    $copied = New-Object System.Collections.Generic.List[string]
    $index = 0
    foreach ($source in $Files) {
        $index++
        $name = [System.IO.Path]::GetFileName($source)
        $targetName = if ($Files.Count -eq 1) { $name } else { "{0:D2}-{1}" -f $index, $name }
        $target = Join-Path $PackageDestination $targetName
        Copy-Item -LiteralPath $source -Destination $target -Force
        $copied.Add($target)
    }
    return $copied.ToArray()
}

if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "NuGet lockfile not found: $lockPath"
}
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "NuGet assets file not found. Run dotnet restore first: $assetsPath"
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null
New-Item -ItemType Directory -Force -Path $managedDestination | Out-Null
New-Item -ItemType Directory -Force -Path $dotnetDestination | Out-Null

Get-ChildItem -LiteralPath (Join-Path $repoRoot "licenses") -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Force
}

$globalPackagesOutput = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve the NuGet global-packages directory."
}
$globalPackagesLine = $globalPackagesOutput | Where-Object { $_ -match 'global-packages:\s*(.+)$' } | Select-Object -First 1
if ($null -eq $globalPackagesLine -or $globalPackagesLine -notmatch 'global-packages:\s*(.+)$') {
    throw "Unexpected dotnet nuget locals output: $globalPackagesOutput"
}
$globalPackagesRoot = [System.IO.Path]::GetFullPath($matches[1].Trim())

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$packages = @{}
foreach ($target in $lock.dependencies.PSObject.Properties) {
    foreach ($package in $target.Value.PSObject.Properties) {
        $version = [string]$package.Value.resolved
        if ([string]::IsNullOrWhiteSpace($version)) { continue }
        $key = $package.Name.ToLowerInvariant()
        if (-not $packages.ContainsKey($key)) {
            $packages[$key] = [ordered]@{
                Name = $package.Name
                Version = $version
                Direct = ($package.Value.type -eq "Direct")
            }
        } elseif ($package.Value.type -eq "Direct") {
            $packages[$key].Direct = $true
        }
    }
}

$approvedExpressions = @("MIT", "LGPL-3.0-or-later")
$rows = New-Object System.Collections.Generic.List[string]
foreach ($key in ($packages.Keys | Sort-Object)) {
    $package = $packages[$key]
    $packageRoot = Join-Path $globalPackagesRoot "$key\$($package.Version)"
    $nuspecPath = Join-Path $packageRoot "$key.nuspec"
    if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
        throw "NuGet metadata is missing for $($package.Name) $($package.Version): $nuspecPath"
    }

    $nuspec = New-Object System.Xml.XmlDocument
    $nuspec.Load($nuspecPath)
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw "Invalid nuspec metadata: $nuspecPath" }

    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $licenseType = if ($null -ne $licenseNode -and $null -ne $licenseNode.Attributes["type"]) {
        $licenseNode.Attributes["type"].Value
    } else { "" }
    $licenseValue = if ($null -ne $licenseNode) { $licenseNode.InnerText.Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($licenseValue)) {
        throw "Package has no auditable NuGet license metadata: $($package.Name) $($package.Version)"
    }
    if ($licenseType -eq "expression" -and $approvedExpressions -notcontains $licenseValue) {
        throw "Unreviewed NuGet license expression '$licenseValue' in $($package.Name) $($package.Version)"
    }

    $declaredFile = if ($licenseType -eq "file") { $licenseValue } else { "" }
    $licenseFiles = @(Get-LicenseFiles -PackageRoot $packageRoot -DeclaredFile $declaredFile)
    $packageFolderName = "$(Get-SafeSegment $package.Name)-$(Get-SafeSegment $package.Version)"
    $packageDestination = Join-Path $managedDestination $packageFolderName
    $copiedFiles = @(Copy-LicenseFiles -Files $licenseFiles -PackageDestination $packageDestination)
    $noticeLinks = @($copiedFiles | ForEach-Object {
        $relative = $_.Substring($destination.TrimEnd('\').Length + 1).Replace('\', '/')
        "[$([System.IO.Path]::GetFileName($_))]($relative)"
    })

    if ($licenseType -eq "expression" -and $licenseValue -eq "MIT") {
        $noticeLinks += "[MIT.txt](MIT.txt)"
    } elseif ($licenseType -eq "expression" -and $licenseValue -eq "LGPL-3.0-or-later") {
        $noticeLinks += "[LGPL-3.0](libwdi-LGPL-3.0.txt)"
        $noticeLinks += "[GPL-3.0](https://www.gnu.org/licenses/gpl-3.0.html)"
    }

    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    $repository = if ($null -ne $repositoryNode -and $null -ne $repositoryNode.Attributes["url"]) {
        $repositoryNode.Attributes["url"].Value
    } else {
        Get-XmlChildText -Parent $metadata -Name "projectUrl"
    }
    $authors = Get-XmlChildText -Parent $metadata -Name "authors"
    $copyright = Get-XmlChildText -Parent $metadata -Name "copyright"
    $owner = @($authors, $copyright) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $relation = if ($package.Direct) { "Direct" } else { "Transitive" }
    $licenseDisplay = "$licenseType`:$licenseValue"
    $row = "| $(Escape-MarkdownCell $package.Name) | $(Escape-MarkdownCell $package.Version) | $relation | $(Escape-MarkdownCell $licenseDisplay) | $(Escape-MarkdownCell ($owner -join '; ')) | $(Escape-MarkdownCell ($noticeLinks -join ', ')) | $(Escape-MarkdownCell $repository) |"
    $rows.Add($row)
}

$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$runtimeRows = New-Object System.Collections.Generic.List[string]
$runtimePackages = @{}
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($dependency in @($framework.Value.downloadDependencies)) {
        if ($null -eq $dependency -or $dependency.name -notmatch '^Microsoft\.(AspNetCore|NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') {
            continue
        }
        $versionText = [string]$dependency.version
        if ($versionText -notmatch '^\[([^,\]]+)') {
            throw "Unexpected runtime pack version: $versionText"
        }
        $runtimePackages[$dependency.name.ToLowerInvariant()] = [ordered]@{
            Name = [string]$dependency.name
            Version = $matches[1]
        }
    }
}
if ($runtimePackages.Count -eq 0) {
    throw "No self-contained win-x64 runtime packs were found in project.assets.json."
}

foreach ($key in ($runtimePackages.Keys | Sort-Object)) {
    $runtimePackage = $runtimePackages[$key]
    $runtimeRoot = Join-Path $globalPackagesRoot "$key\$($runtimePackage.Version)"
    if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) {
        throw "Runtime package is missing from the NuGet cache: $runtimeRoot"
    }
    $licenseFiles = @(Get-LicenseFiles -PackageRoot $runtimeRoot -DeclaredFile "")
    if ($licenseFiles.Count -eq 0) {
        throw "Runtime package has no bundled license or notice files: $($runtimePackage.Name) $($runtimePackage.Version)"
    }
    $runtimeFolder = Join-Path $dotnetDestination "$(Get-SafeSegment $runtimePackage.Name)-$(Get-SafeSegment $runtimePackage.Version)"
    $copiedFiles = @(Copy-LicenseFiles -Files $licenseFiles -PackageDestination $runtimeFolder)
    $links = @($copiedFiles | ForEach-Object {
        $relative = $_.Substring($destination.TrimEnd('\').Length + 1).Replace('\', '/')
        "[$([System.IO.Path]::GetFileName($_))]($relative)"
    })
    $runtimeRows.Add("| $(Escape-MarkdownCell $runtimePackage.Name) | $(Escape-MarkdownCell $runtimePackage.Version) | $(Escape-MarkdownCell ($links -join ', ')) |")
}

$lockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
$report = New-Object System.Collections.Generic.List[string]
$report.Add("# Managed dependency and .NET runtime notices")
$report.Add("")
$report.Add("Generated from ``ZeroCue.DataProbe/packages.lock.json`` (SHA-256 ``$lockHash``) and the restored win-x64 runtime packs. Package authors, copyright fields, license expressions, repositories, and bundled notice files come from the corresponding NuGet packages.")
$report.Add("")
$report.Add("The inventory intentionally covers the complete locked dependency closure. Platform selection may omit some non-Windows native assets from the final executable, but their notices are retained conservatively.")
$report.Add("")
$report.Add("## Managed NuGet dependency closure")
$report.Add("")
$report.Add("| Package | Version | Relationship | Declared license | Authors / copyright | Bundled notices | Repository |")
$report.Add("|---|---:|---|---|---|---|---|")
foreach ($row in $rows) { $report.Add($row) }
$report.Add("")
$report.Add("## Self-contained .NET runtime packs")
$report.Add("")
$report.Add("| Runtime package | Version | Bundled license and third-party notices |")
$report.Add("|---|---:|---|")
foreach ($row in $runtimeRows) { $report.Add($row) }
$report.Add("")
$report.Add("Microsoft documents that self-contained deployments embed runtime components in the application. The license files above are copied from the exact runtime packs selected by the .NET SDK for this build.")

$reportPath = Join-Path $destination "managed-dependencies.md"
[System.IO.File]::WriteAllLines($reportPath, $report, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Third-party audit complete: $($packages.Count) managed packages and $($runtimePackages.Count) .NET runtime packs."
Write-Host "Notice bundle: $destination"
