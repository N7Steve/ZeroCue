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
    '$targetDevices',
    '$initialWinUsbDevices',
    '$legacyHardwareIds',
    'config.ObsoleteBindings.Select',
    '$hasLegacyIdentity',
    '$hasScopedZadigIdentity',
    '$hasOrphanedZeroCueIdentity',
    '$isLegacyZeroCueWinUsb',
    'WinUSBDeviceClassReg',
    'AddService\\s*=\\s*WinUSB',
    'DeviceName\\s*=\\s*\"SCUF\"',
    'SourceName\\s*=\\s*\"SCUF Install Disk\"',
    'Provider\\s*=\\s*\"libwdi\"',
    'Class\\s*=\\s*\"USBDevice\"',
    'CatalogFile\\s*=\\s*usb_device\\.cat',
    'DriverVer\\s*=\\s*04/18/2019',
    'VendorName\\s*=\\s*\"Corsair\"',
    'DeviceName\\s*=\\s*\"SCUF[^\"]+\"',
    'DriverVer\\s*=\\s*06/02/2012',
    "DEVPKEY_Device_DriverInfPath",
    '$verifiedInfs -contains $driverInf',
    'pnputil /remove-device',
    'pnputil /delete-driver $infName /uninstall /force',
    "content -notmatch 'ZeroCue'",
    '$remainingWinUsbDevices',
    'Get-PnpDevice -PresentOnly',
    '$hasTargetId -and $_.Service -match ''WINUSB''',
    '$failureCount += $remainingWinUsbDevices.Count',
    '$verifiedInfs.Count -eq 0 -and $initialWinUsbDevices.Count -gt 0',
    'packages=$($verifiedInfs -join '','')',
    'remaining=$($remainingWinUsbDevices.InstanceId -join ''|'')'
)

foreach ($fragment in $requiredFragments) {
    if ($restoreSource.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required driver restoration boundary is missing: $fragment"
    }
}

$migrationStartMarker = "private static void AppendObsoleteBindingMigrationScript"
$migrationEndMarker = "private async Task<bool> ValidateReceiverWinUsbTopologyAsync"
$migrationStart = $source.IndexOf($migrationStartMarker, [System.StringComparison]::Ordinal)
$migrationEnd = $source.IndexOf($migrationEndMarker, $migrationStart, [System.StringComparison]::Ordinal)
if ($migrationStart -lt 0 -or $migrationEnd -le $migrationStart) {
    throw "Could not locate the obsolete receiver binding migration for safety verification."
}

$migrationSource = $source.Substring($migrationStart, $migrationEnd - $migrationStart)
$requiredMigrationFragments = @(
    '$obsoleteDevices',
    'DEVPKEY_Device_DriverInfPath',
    '$hasExactHardwareId',
    '$isWinUsbPackage',
    '$isManifestOwned',
    '$isZeroCueOwned',
    '$hasLegacyIdentity',
    '$hasScopedZadigIdentity',
    '$containsRequiredReceiverBinding',
    '$isVerifiedObsoletePackage',
    'pnputil.exe /remove-device $obsoleteDevice.InstanceId',
    'pnputil.exe /delete-driver $driverInf /uninstall /force',
    '$migrationFailures++'
)

foreach ($fragment in $requiredMigrationFragments) {
    if ($migrationSource.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required obsolete binding migration boundary is missing: $fragment"
    }
}

if ($migrationSource -match '/subtree') {
    throw "The obsolete receiver binding migration must remain limited to the exact PnP instance."
}

$requiredPortableTargets = @(
    '"0x1B1C"',
    '"0x3A05"',
    '"0x3A04"',
    '"0x3A08"',
    '"0x3A09"',
    'new DriverBinding("0x3A08", 4, "interface 4 (MI_04)")',
    'new DriverBinding("0x3A08", 3, "interface 3 (MI_03)")',
    'new ObsoleteDriverBinding("0x3A08", 0, "obsolete interface 0 (MI_00)")',
    'new DriverBinding("0x3A09", null, "active receiver device")',
    'new DriverBinding("0x3A05", 0, "interface 0 (MI_00)")',
    'new DriverBinding("0x3A05", 4, "interface 4 (MI_04)")',
    'new DriverBinding("0x3A05", 3, "interface 3 (MI_03)")',
    'new DriverBinding("0x3A04", 0, "interface 0 (MI_00)")',
    'new DriverBinding("0x3A04", 4, "interface 4 (MI_04)")',
    'new DriverBinding("0x3A04", 3, "interface 3 (MI_03)")',
    'manifestPids.IsSubsetOf(expectedPids)',
    '$candidateDevices.Count -gt 0',
    'if ($interfaceId -ge 0) { $argsArray += @(''-i'', $interfaceId) }',
    "'-o', '15000'",
    'taskkill.exe /PID $proc.Id /T /F',
    'install diagnostics retained at:',
    'resultCodes.Length == expectedResultCount',
    'Where(package => File.Exists(Path.Combine(windowsInfDirectory, package)))',
    'DeleteOwnedDriverPackageManifest(config);',
    'ValidateReceiverWinUsbTopologyAsync()',
    'MI_04 does not expose 64-byte OUT 0x02 / IN 0x82 pipes through WinUSB.',
    'MI_03 does not expose a 64-byte IN 0x81 pipe through WinUSB.'
)

foreach ($fragment in $requiredPortableTargets) {
    if ($source.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required portable driver target is missing: $fragment"
    }
}

if ($source -match 'new DriverBinding\([^\r\n]*[A-Fa-f0-9]{64}') {
    throw "Machine-derived driver fingerprints must not be stored in driver target configuration."
}

if ($source -match 'Get-PnpDevice[^\r\n]*-InstanceId[^\r\n]*\*') {
    throw "Get-PnpDevice -InstanceId does not support wildcard matching; enumerate present devices and filter InstanceId instead."
}

if ($source -match '\$null -ne \$candidateDevices') {
    throw "An empty PowerShell array is not null; PID selection must require at least one matching present device."
}

if ($source -match 'new DriverBinding\(\"0x3A09\",\s*[0-9]') {
    throw "PID 3A09 is a whole-device HID receiver state and must not be installed as a composite MI interface."
}

if ($source -match 'new DriverBinding\(\"0x3A08\",\s*0') {
    throw "PID 3A08 MI_00 is not a receiver transport and must only appear as an obsolete migration target."
}

if ($source -match 'Read-Host') {
    throw "The elevated driver workflow must not block indefinitely waiting for console input."
}

Write-Host "Driver restoration is limited to manifest-owned packages or legacy packages with the exact portable INF signature, hardware IDs, and PnP instances."
