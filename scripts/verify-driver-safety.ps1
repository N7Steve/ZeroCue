[CmdletBinding()]
param()

# Structural checks that the compiler and unit tests cannot express.
# Behavioural driver rules (device catalog, package ownership, restore
# boundaries) are covered by tests/ZeroCue.Tests (DriverCatalogTests,
# InfClassifierTests, DriverOperationEngineTests); run `dotnet test` too.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$servicesRoot = Join-Path $repoRoot "ZeroCue.DataProbe\Services"
$sources = Get-ChildItem -LiteralPath $servicesRoot -Recurse -Filter *.cs |
    ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Text = Get-Content -LiteralPath $_.FullName -Raw } }

function Get-Source([string]$relativePath) {
    $path = Join-Path $servicesRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected source file is missing: $relativePath"
    }

    Get-Content -LiteralPath $path -Raw
}

# 1. Elevation only through the in-process helper; never generated PowerShell.
foreach ($source in $sources) {
    if ($source.Text -match 'Verb\s*=\s*"runas"' -and $source.Path -notlike '*DriverAutomationService.cs') {
        throw "Only DriverAutomationService may request elevation: $($source.Path)"
    }

    if ($source.Text -match '-ExecutionPolicy' -or $source.Text -match 'FileName\s*=\s*"powershell\.exe"') {
        throw "Driver operations must not run PowerShell scripts: $($source.Path)"
    }
}

$automation = Get-Source "DriverAutomationService.cs"
if ($automation -notmatch 'DriverHelperArguments\.Build') {
    throw "Elevation must launch the --driver-helper mode of the application."
}

# 2. The helper verifies wdi-simple against the same pinned hash as the release assets.
$helper = Get-Source "Drivers\ElevatedDriverHelper.cs"
$assetScript = Get-Content -LiteralPath (Join-Path $repoRoot "scripts\verify-runtime-assets.ps1") -Raw
$pinned = [regex]::Match($helper, 'WdiSimpleSha256\s*=\s*"(?<hash>[0-9A-F]{64})"').Groups['hash'].Value
if (-not $pinned) {
    throw "ElevatedDriverHelper must pin the SHA-256 of wdi-simple.exe."
}

if ($assetScript -notmatch [regex]::Escape($pinned)) {
    throw "The helper's pinned wdi-simple hash does not match scripts/verify-runtime-assets.ps1."
}

foreach ($fragment in @('SHA256.HashData', 'CreateProtectedWorkDirectory', 'SetAccessRuleProtection', 'LOAD_LIBRARY_SEARCH_SYSTEM32', 'OperationMutexName')) {
    if ($helper.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Elevated helper safety boundary is missing: $fragment"
    }
}

# 3. Package removal is always gated by INF classification, never by name patterns.
$engine = Get-Source "Drivers\DriverOperationEngine.cs"
if ($engine -match '/subtree') {
    throw "Driver operations must stay limited to exact PnP instances (/subtree is forbidden)."
}

$deleteCalls = [regex]::Matches($engine, '"/delete-driver"').Count
if ($deleteCalls -lt 1 -or [regex]::Matches($engine, 'InfClassifier\.Classify').Count -lt 2) {
    throw "Driver package deletion must be preceded by InfClassifier ownership checks."
}

Write-Host "Driver safety boundaries verified."
