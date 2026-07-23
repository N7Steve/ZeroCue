[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$servicePath = Join-Path $repoRoot "ZeroCue.DataProbe\Services\DriverAutomationService.cs"
$source = Get-Content -LiteralPath $servicePath -Raw

$startMarker = "private async Task<bool> RestoreDefaultDriversAsync"
$endMarker = "private static Dictionary<string, string> ParseKeyValueResult"
$start = $source.IndexOf($startMarker, [System.StringComparison]::Ordinal)
$end = $source.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) {
    throw "Could not locate the driver restoration implementation for safety verification."
}

$restoreSource = $source.Substring($start, $end - $start)
$forbiddenFragments = @(
    "libwdi|WinUSB",
    "/subtree",
    "Get-FileHash",
    "LegacyInfSha256"
)

foreach ($fragment in $forbiddenFragments) {
    if ($restoreSource.IndexOf($fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Unsafe broad driver restoration fragment found: $fragment"
    }
}

$requiredFragments = @(
    "LoadOwnedDriverPackages(config)",
    "allowLegacyDiscovery",
    '$targetDevices',
    '$legacyHardwareIds',
    '$hasLegacyIdentity',
    '$isLegacyZeroCueWinUsb',
    'WinUSBDeviceClassReg',
    'AddService\\s*=\\s*WinUSB',
    'DeviceName\\s*=\\s*\"SCUF\"',
    'SourceName\\s*=\\s*\"SCUF Install Disk\"',
    'Provider\\s*=\\s*\"libwdi\"',
    'Class\\s*=\\s*\"USBDevice\"',
    'CatalogFile\\s*=\\s*usb_device\\.cat',
    'DriverVer\\s*=\\s*04/18/2019',
    "DEVPKEY_Device_DriverInfPath",
    '$verifiedInfs -contains $driverInf',
    'pnputil /remove-device',
    'pnputil /delete-driver $infName /uninstall /force',
    "content -notmatch 'ZeroCue'"
)

foreach ($fragment in $requiredFragments) {
    if ($restoreSource.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required driver restoration boundary is missing: $fragment"
    }
}

$requiredPortableTargets = @(
    '"0x1B1C"',
    '"0x3A05"',
    '"0x3A04"',
    '"0x3A08"',
    '"0x3A09"',
    'new DriverInterface(0, "MI_00")',
    'new DriverInterface(4, "MI_04")',
    'new DriverInterface(3, "MI_03")',
    'manifestPids.IsSubsetOf(expectedPids)',
    '$candidateDevices.Count -gt 0'
)

foreach ($fragment in $requiredPortableTargets) {
    if ($source.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required portable driver target is missing: $fragment"
    }
}

if ($source -match 'new DriverInterface\([^\r\n]*[A-Fa-f0-9]{64}') {
    throw "Machine-derived driver fingerprints must not be stored in driver target configuration."
}

if ($source -match 'Get-PnpDevice[^\r\n]*-InstanceId[^\r\n]*\*') {
    throw "Get-PnpDevice -InstanceId does not support wildcard matching; enumerate present devices and filter InstanceId instead."
}

if ($source -match '\$null -ne \$candidateDevices') {
    throw "An empty PowerShell array is not null; PID selection must require at least one matching present device."
}

Write-Host "Driver restoration is limited to manifest-owned packages or legacy packages with the exact portable INF signature, hardware IDs, and PnP instances."
