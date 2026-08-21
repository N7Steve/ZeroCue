using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCue.DataProbe.Services
{
    public class DriverAutomationService
    {
        private void Log(string msg)
        {
            ZeroCueLog.Communication($"[DRIVER] {msg}");
        }

        public Task<bool> InstallWinUsbDriversAsync()
        {
            return InstallWinUsbDriversAsync(DriverTarget.Gamepad);
        }

        public Task<bool> InstallReceiverWinUsbDriversAsync()
        {
            return InstallWinUsbDriversAsync(DriverTarget.Receiver);
        }

        public Task<bool> RestoreDefaultDriversAsync()
        {
            return RestoreDefaultDriversAsync(DriverTarget.Gamepad);
        }

        public Task<bool> RestoreReceiverDefaultDriversAsync()
        {
            return RestoreDefaultDriversAsync(DriverTarget.Receiver);
        }

        private async Task<bool> InstallWinUsbDriversAsync(DriverTarget target)
        {
            var config = DriverTargetConfig.For(target);
            var previouslyOwnedPackages = LoadOwnedDriverPackages(config);
            string wdiPath = ZeroCuePaths.GetAppPath("Assets", "wdi-simple.exe");
            if (!File.Exists(wdiPath))
            {
                Log("wdi-simple.exe not found in Assets folder.");
                return false;
            }

            string workDir = Path.Combine(Path.GetTempPath(), "ZeroCue", "driver", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            string ps1Path = Path.Combine(workDir, $"zerocue_install_{config.LogName}_drivers.ps1");
            string resultPath = Path.Combine(workDir, $"zerocue_install_{config.LogName}_result.txt");
            string wdiLog = Path.Combine(workDir, $"zerocue_wdi_{config.LogName}_output.txt");
            bool preserveDiagnostics = false;

            try
            {
                if (File.Exists(resultPath))
                    File.Delete(resultPath);

                string runId = DateTime.Now.ToString("yyyyMMddHHmmss");
                string tempDirPrefix = Path.Combine(workDir, $"zerocue_wdi_{config.LogName}_{runId}");
                string previouslyOwnedInfValues = string.Join(",", previouslyOwnedPackages.Select(PowerShellLiteral));
                string diagnosticVendorPatternValues = string.Join(",", config.Variants
                    .Select(variant => $"USB\\VID_{variant.VidValue}*")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(PowerShellLiteral));

                var ps1 = new StringBuilder();
                ps1.AppendLine($"Write-Output '=== WDI Install Log {config.DisplayName} ({DateTime.Now:yyyyMMddHHmmss}) ===' > {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("function Get-PublishedInfNames {");
                ps1.AppendLine("    @(Get-ChildItem -Path (Join-Path $env:windir 'INF\\oem*.inf') -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)");
                ps1.AppendLine("}");
                ps1.AppendLine("function Write-PnpSnapshot {");
                ps1.AppendLine("    param([string]$label, [object[]]$devices)");
                ps1.AppendLine("    $deviceList = @($devices)");
                ps1.AppendLine($"    Write-Output \"PnP snapshot label=$label count=$($deviceList.Count)\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("    foreach ($device in $deviceList | Sort-Object InstanceId) {");
                ps1.AppendLine("        $driverInf = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data");
                ps1.AppendLine("        $hardwareIds = ((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data -join ';')");
                ps1.AppendLine("        $compatibleIds = ((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_CompatibleIds' -ErrorAction SilentlyContinue).Data -join ';')");
                ps1.AppendLine("        $parent = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data");
                ps1.AppendLine("        $problemCode = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data");
                ps1.AppendLine("        $problemStatus = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ProblemStatus' -ErrorAction SilentlyContinue).Data");
                ps1.AppendLine("        $locationPaths = ((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_LocationPaths' -ErrorAction SilentlyContinue).Data -join ';')");
                ps1.AppendLine("        $containerId = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ContainerId' -ErrorAction SilentlyContinue).Data");
                ps1.AppendLine("        $busDescription = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_BusReportedDeviceDesc' -ErrorAction SilentlyContinue).Data");
                ps1.AppendLine($"        Write-Output \"PnP node label=$label present=$($device.Present) friendlyName=$($device.FriendlyName) busDescription=$busDescription instance=$($device.InstanceId) status=$($device.Status) class=$($device.Class) service=$($device.Service) driverInf=$driverInf problemCode=$problemCode problemStatus=$problemStatus parent=$parent containerId=$containerId locationPaths=$locationPaths hardwareIds=$hardwareIds compatibleIds=$compatibleIds\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("    }");
                ps1.AppendLine("}");
                ps1.AppendLine("$publishedInfsBefore = @(Get-PublishedInfNames)");
                ps1.AppendLine($"$knownOwnedInfs = @({previouslyOwnedInfValues})");
                ps1.AppendLine($"$diagnosticVendorPatterns = @({diagnosticVendorPatternValues})");
                ps1.AppendLine("$targets = @(");
                foreach (var variant in config.Variants)
                {
                    foreach (var pidValue in variant.PidValues)
                    {
                        string experimental = variant.IsExperimental ? "$true" : "$false";
                        ps1.AppendLine($"    [pscustomobject]@{{ VidValue={PowerShellLiteral(variant.VidValue)}; Vid={PowerShellLiteral(variant.Vid)}; PidValue={PowerShellLiteral(pidValue)}; Variant={PowerShellLiteral(variant.Name)}; DeviceName={PowerShellLiteral(variant.DeviceName)}; Experimental={experimental} }}");
                    }
                }
                ps1.AppendLine(")");
                ps1.AppendLine("$supportedIdentities = @($targets | ForEach-Object { 'VID_' + $_.VidValue + '&PID_' + $_.PidValue })");
                ps1.AppendLine("$selectedTarget = $null");
                ps1.AppendLine("$selectedDevices = @()");
                ps1.AppendLine("for ($selectionAttempt = 1; $selectionAttempt -le 10 -and $null -eq $selectedTarget; $selectionAttempt++) {");
                ps1.AppendLine("    if ($selectionAttempt -eq 1 -or $selectionAttempt -eq 5) {");
                ps1.AppendLine("        $selectionScanOutput = & pnputil.exe /scan-devices 2>&1");
                ps1.AppendLine("        $selectionScanExitCode = $LASTEXITCODE");
                ps1.AppendLine($"        Write-Output \"PnP selection scan attempt=$selectionAttempt exitCode=$selectionScanExitCode output=$($selectionScanOutput -join ' | ')\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("    }");
                ps1.AppendLine("    if ($selectionAttempt -eq 1 -or $selectionAttempt -eq 5 -or $selectionAttempt -eq 10) {");
                ps1.AppendLine("        $presentVendorDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine("            $instanceId = $_.InstanceId");
                ps1.AppendLine("            @($diagnosticVendorPatterns | Where-Object { $instanceId -like $_ }).Count -gt 0");
                ps1.AppendLine("        })");
                ps1.AppendLine("        Write-PnpSnapshot -label \"selection-attempt-$selectionAttempt-present-vendor-nodes\" -devices $presentVendorDevices");
                ps1.AppendLine("    }");
                ps1.AppendLine("    foreach ($candidateTarget in $targets) {");
                ps1.AppendLine("        $candidatePattern = \"USB\\VID_$($candidateTarget.VidValue)&PID_$($candidateTarget.PidValue)*\"");
                ps1.AppendLine("        $candidateDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like $candidatePattern })");
                ps1.AppendLine("        if ($candidateDevices.Count -gt 0) { $selectedTarget = $candidateTarget; $selectedDevices = $candidateDevices; break }");
                ps1.AppendLine("    }");
                ps1.AppendLine($"    if ($null -eq $selectedTarget) {{ Write-Output \"Driver target selection attempt=$selectionAttempt/10 found=0 identities=$($supportedIdentities -join ',')\" >> {PowerShellLiteral(wdiLog)} }}");
                ps1.AppendLine("    if ($null -eq $selectedTarget -and $selectionAttempt -lt 10) { Start-Sleep -Seconds 1 }");
                ps1.AppendLine("}");
                ps1.AppendLine("if ($null -eq $selectedTarget) {");
                ps1.AppendLine("    $allVendorDevices = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine("        $instanceId = $_.InstanceId");
                ps1.AppendLine("        @($diagnosticVendorPatterns | Where-Object { $instanceId -like $_ }).Count -gt 0");
                ps1.AppendLine("    })");
                ps1.AppendLine("    Write-PnpSnapshot -label 'selection-failed-all-vendor-nodes-including-phantoms' -devices $allVendorDevices");
                ps1.AppendLine($"    Write-Output \"No present {config.DisplayName} matched supported identities: $($supportedIdentities -join ', ').\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine($"    Write-Output 'results=' > {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"    Write-Output 'packages=' >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"    Write-Output 'vid=' >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"    Write-Output 'pid=' >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"    Write-Output 'variant=' >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine("    exit 2");
                ps1.AppendLine("}");
                ps1.AppendLine("$selectedVidValue = $selectedTarget.VidValue");
                ps1.AppendLine("$selectedVid = $selectedTarget.Vid");
                ps1.AppendLine("$selectedPidValue = $selectedTarget.PidValue");
                ps1.AppendLine("$selectedPid = \"0x$selectedPidValue\"");
                ps1.AppendLine("$selectedVariant = $selectedTarget.Variant");
                ps1.AppendLine("$selectedDeviceName = $selectedTarget.DeviceName");
                ps1.AppendLine("$selectedExperimental = $selectedTarget.Experimental");
                ps1.AppendLine($"Write-Output \"Selected variant=$selectedVariant experimental=$selectedExperimental VID_$selectedVidValue&PID_$selectedPidValue.\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("Write-PnpSnapshot -label 'selected-before-install' -devices $selectedDevices");
                ps1.AppendLine("Write-Host '=======================================' -ForegroundColor Cyan");
                ps1.AppendLine($"Write-Host '  Installing WinUSB: {config.DisplayName}' -ForegroundColor Cyan");
                ps1.AppendLine("Write-Host '=======================================' -ForegroundColor Cyan");
                ps1.AppendLine("Write-Host 'Preparing the environment (closing iCUE)...' -ForegroundColor Yellow");
                ps1.AppendLine($"Write-Output 'Killing Corsair/iCUE processes and services...' >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine(BuildCorsairShutdownScript());
                ps1.AppendLine("Start-Sleep -Seconds 3");
                ps1.AppendLine("Write-Host '[OK] Processes and services stopped.' -ForegroundColor Green");
                AppendObsoleteBindingMigrationScript(ps1, config, wdiLog);
                ps1.AppendLine("Write-Host 'Installing drivers... Accept any Windows security prompts that appear.' -ForegroundColor Yellow");

                foreach (var variant in config.Variants)
                {
                    foreach (var bindingSuffix in variant.Bindings
                        .Select(binding => binding.InterfaceId is int id ? $"mi{id}" : "device")
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        ps1.AppendLine($"New-Item -ItemType Directory -Force -Path {PowerShellLiteral($"{tempDirPrefix}_{variant.Key}_{bindingSuffix}")} | Out-Null");
                    }
                }

                ps1.AppendLine("function Install-WdiDriver {");
                ps1.AppendLine("    param([string]$tempDir, [int]$interfaceId, [string]$miString)");
                ps1.AppendLine("    Write-Host \"-> Installing $($miString)...\" -ForegroundColor Cyan");
                ps1.AppendLine($"    Write-Output \"Running $($miString)...\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("    $proc = $null");
                ps1.AppendLine("    $procExitText = '<not started>'");
                ps1.AppendLine("    try {");
                ps1.AppendLine("        $wdiOut = Join-Path -Path $tempDir -ChildPath 'wdi_out.log'");
                ps1.AppendLine("        $wdiErr = Join-Path -Path $tempDir -ChildPath 'wdi_err.log'");
                ps1.AppendLine("        $argsArray = @('-n', $selectedDeviceName, '-v', $selectedVid, '-p', $selectedPid)");
                ps1.AppendLine("        if ($interfaceId -ge 0) { $argsArray += @('-i', $interfaceId) }");
                ps1.AppendLine("        $argsArray += @('-t', '0', '-s', '-o', '15000', '-d', $tempDir)");
                ps1.AppendLine($"        Write-Output \"wdi-simple command binding=$miString args=$($argsArray -join ' ') tempDir=$tempDir\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine($"        $proc = Start-Process -FilePath {PowerShellLiteral(wdiPath)} -ArgumentList $argsArray -PassThru -NoNewWindow -RedirectStandardOutput $wdiOut -RedirectStandardError $wdiErr");
                ps1.AppendLine("        $maxWait = 60");
                ps1.AppendLine("        $waited = 0");
                ps1.AppendLine("        $miHex = '{0:X2}' -f $interfaceId");
                ps1.AppendLine("        $targetInstancePattern = if ($interfaceId -ge 0) {");
                ps1.AppendLine("            \"USB\\VID_$selectedVidValue&PID_$selectedPidValue&MI_$miHex\\*\"");
                ps1.AppendLine("        } else {");
                ps1.AppendLine("            \"USB\\VID_$selectedVidValue&PID_$selectedPidValue\\*\"");
                ps1.AppendLine("        }");
                ps1.AppendLine("        while (-not $proc.HasExited -and $waited -lt $maxWait) {");
                ps1.AppendLine("            Start-Sleep -Seconds 3");
                ps1.AppendLine("            $waited += 3");
                ps1.AppendLine("            $devs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine("                $_.InstanceId -like $targetInstancePattern");
                ps1.AppendLine("            })");
                ps1.AppendLine("            if ($devs.Count -gt 0) {");
                ps1.AppendLine("                if ($devs.Service -match 'WINUSB' -or $devs.Service -match 'WinUSB') {");
                ps1.AppendLine($"                    Write-Output \"WINUSB detected on $($miString). Stopping the unresponsive wdi-simple process.\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("                    & taskkill.exe /PID $proc.Id /T /F *> $null");
                ps1.AppendLine("                    break");
                ps1.AppendLine("                }");
                ps1.AppendLine("            }");
                ps1.AppendLine("        }");
                ps1.AppendLine("        if (-not $proc.HasExited) {");
                ps1.AppendLine($"            Write-Output \"Timeout reached for $($miString). Force killing.\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("            & taskkill.exe /PID $proc.Id /T /F *> $null");
                ps1.AppendLine("        }");
                ps1.AppendLine("        try { $null = $proc.WaitForExit(5000); $proc.Refresh() } catch { }");
                ps1.AppendLine("        $procExitCode = if ($proc.HasExited) { $proc.ExitCode } else { $null }");
                ps1.AppendLine("        $procExitText = if ($null -eq $procExitCode) { '<unavailable>' } else { [string]$procExitCode }");
                ps1.AppendLine($"        Write-Output \"wdi-simple completed binding=$miString processId=$($proc.Id) exitCode=$procExitText waitedSeconds=$waited\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine($"        if (Test-Path -LiteralPath $wdiOut) {{ Write-Output \"--- wdi-simple stdout tail binding=$miString ---\" >> {PowerShellLiteral(wdiLog)}; Get-Content -LiteralPath $wdiOut -Tail 200 -ErrorAction SilentlyContinue | Out-File -FilePath {PowerShellLiteral(wdiLog)} -Append -Encoding utf8 }}");
                ps1.AppendLine($"        if (Test-Path -LiteralPath $wdiErr) {{ Write-Output \"--- wdi-simple stderr tail binding=$miString ---\" >> {PowerShellLiteral(wdiLog)}; Get-Content -LiteralPath $wdiErr -Tail 200 -ErrorAction SilentlyContinue | Out-File -FilePath {PowerShellLiteral(wdiLog)} -Append -Encoding utf8 }}");
                ps1.AppendLine("        Start-Sleep -Seconds 2");
                ps1.AppendLine("        $finalDevs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine("            $_.InstanceId -like $targetInstancePattern");
                ps1.AppendLine("        })");
                ps1.AppendLine("        Write-PnpSnapshot -label \"wdi-final-$miString\" -devices $finalDevs");
                ps1.AppendLine("        if ($finalDevs.Count -gt 0 -and ($finalDevs.Service -match 'WINUSB' -or $finalDevs.Service -match 'WinUSB')) {");
                ps1.AppendLine("            $ret = 0");
                ps1.AppendLine("        } else {");
                ps1.AppendLine("            $ret = -1");
                ps1.AppendLine($"            Write-Output \"Failed: WINUSB not applied to $($miString). ExitCode: $procExitText\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("        }");
                ps1.AppendLine("    } catch {");
                ps1.AppendLine($"        Write-Output \"Error $($miString): $($_.Exception.Message)\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("        $ret = -999");
                ps1.AppendLine("    }");
                ps1.AppendLine("    if ($ret -eq 0) {");
                ps1.AppendLine("        Write-Host \"   [OK] $miString installed successfully.\" -ForegroundColor Green");
                ps1.AppendLine("        return 0");
                ps1.AppendLine("    } else {");
                ps1.AppendLine("        Write-Host \"   [ERROR] $miString failed (see $wdiErr). ExitCode: $procExitText\" -ForegroundColor Red");
                ps1.AppendLine("        return -1");
                ps1.AppendLine("    }");
                ps1.AppendLine("}");

                ps1.AppendLine("$resultCodes = @()");
                foreach (var variant in config.Variants)
                {
                    foreach (var binding in variant.Bindings)
                    {
                        int interfaceId = binding.InterfaceId ?? -1;
                        string bindingSuffix = binding.InterfaceId is int id ? $"mi{id}" : "device";
                        ps1.AppendLine($"if ($selectedVidValue -eq {PowerShellLiteral(variant.VidValue)} -and $selectedPidValue -eq {PowerShellLiteral(binding.PidValue)}) {{");
                        ps1.AppendLine($"    $resultCodes += Install-WdiDriver -tempDir {PowerShellLiteral($"{tempDirPrefix}_{variant.Key}_{bindingSuffix}")} -interfaceId {interfaceId} -miString {PowerShellLiteral(binding.Name)}");
                        ps1.AppendLine("    Start-Sleep -Seconds 4");
                        ps1.AppendLine("}");
                    }
                }
                ps1.AppendLine("if ($migrationFailures -gt 0) { $resultCodes += -1 }");

                if (target == DriverTarget.Receiver)
                {
                    AppendReceiverBindingReenumerationScript(ps1, config, wdiLog);
                }

                ps1.AppendLine("$publishedInfsAfter = @(Get-PublishedInfNames)");
                ps1.AppendLine("$createdInfs = @($publishedInfsAfter | Where-Object { $publishedInfsBefore -notcontains $_ })");
                ps1.AppendLine("$ownedInfs = @()");
                ps1.AppendLine("foreach ($infName in $createdInfs) {");
                ps1.AppendLine("    $infPath = Join-Path (Join-Path $env:windir 'INF') $infName");
                ps1.AppendLine("    $content = Get-Content -LiteralPath $infPath -Raw -ErrorAction SilentlyContinue");
                ps1.AppendLine("    if ($content -match 'ZeroCue' -and $content -match \"VID_$selectedVidValue&PID_$selectedPidValue\") { $ownedInfs += $infName }");
                ps1.AppendLine("}");
                ps1.AppendLine("$selectedPattern = \"USB\\VID_$selectedVidValue&PID_$selectedPidValue*\"");
                ps1.AppendLine("$afterDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like $selectedPattern })");
                ps1.AppendLine("Write-PnpSnapshot -label 'selected-after-install' -devices $afterDevices");
                ps1.AppendLine($"Write-Output \"results=$($resultCodes -join ',')\" > {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"packages=$($ownedInfs -join ',')\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"vid=$selectedVidValue\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"pid=$selectedPidValue\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"variant=$selectedVariant\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"experimental=$selectedExperimental\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine(BuildCorsairRestartScript());
                ps1.AppendLine("Write-Host '=======================================' -ForegroundColor Cyan");
                ps1.AppendLine("if ($resultCodes.Count -gt 0 -and $resultCodes -notcontains -1) {");
                ps1.AppendLine("    Write-Host '[SUCCESS] Installation completed successfully.' -ForegroundColor Green");
                ps1.AppendLine("    Write-Host 'This window will close automatically...' -ForegroundColor DarkGray");
                ps1.AppendLine("    Start-Sleep -Seconds 3");
                ps1.AppendLine("} else {");
                ps1.AppendLine("    Write-Host '[WARNING] Installation completed with errors.' -ForegroundColor Red");
                ps1.AppendLine("    Write-Host 'Review the log path shown above. This window will close automatically...' -ForegroundColor Yellow");
                ps1.AppendLine("    Start-Sleep -Seconds 5");
                ps1.AppendLine("}");
                ps1.AppendLine("Stop-Process -Name 'wdi-simple' -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("Stop-Process -Name 'installer_x64' -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("if ($resultCodes.Count -eq 0 -or $resultCodes -contains -1) { exit 1 }");
                ps1.AppendLine("exit 0");
                File.WriteAllText(ps1Path, ps1.ToString());
                Log($"Created {config.LogName} install script at: {ps1Path}");
                if (!ValidatePowerShellScriptSyntax(ps1Path, $"{config.LogName} install"))
                {
                    preserveDiagnostics = true;
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(ps1Path);

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    Log($"{config.LogName} install script ExitCode: {process.ExitCode}");
                }

                if (File.Exists(resultPath))
                {
                    var result = ParseKeyValueResult(resultPath);
                    var resultCodes = GetCsvValues(result, "results");
                    var newlyOwnedPackages = GetCsvValues(result, "packages")
                        .Where(IsPublishedInfName)
                        .ToArray();

                    string selectedVid = result.TryGetValue("vid", out var vidValue) ? vidValue : string.Empty;
                    string selectedPid = result.TryGetValue("pid", out var pidValue) ? pidValue : string.Empty;
                    string selectedVariantName = result.TryGetValue("variant", out var variantValue) ? variantValue : string.Empty;
                    var selectedVariant = config.FindVariant(selectedVid, selectedPid);
                    Log($"{config.LogName} install results: variant={selectedVariantName} VID=0x{selectedVid} PID=0x{selectedPid} results={string.Join(",", resultCodes)} ownedPackages={string.Join(",", newlyOwnedPackages)}");
                    int expectedResultCount = selectedVariant?.Bindings.Count(binding =>
                        string.Equals(binding.PidValue, selectedPid, StringComparison.OrdinalIgnoreCase)) ?? 0;
                    bool success = expectedResultCount > 0 &&
                        resultCodes.Length == expectedResultCount &&
                        resultCodes.All(code => code == "0");
                    bool requiresReceiverTopologyValidation = target == DriverTarget.Receiver &&
                        selectedVariant?.Bindings.Any(binding => binding.InterfaceId == 4) == true &&
                        selectedVariant.Bindings.Any(binding => binding.InterfaceId == 3);
                    if (success && requiresReceiverTopologyValidation)
                    {
                        success = await ValidateReceiverWinUsbTopologyAsync(selectedVid, selectedPid, selectedVariant!);
                    }

                    if (success)
                    {
                        SaveOwnedDriverPackages(config, newlyOwnedPackages);
                    }
                    else if (target == DriverTarget.Receiver && selectedVariant != null && resultCodes.Length > 0)
                    {
                        var scopedConfig = config.ScopeToIdentity(selectedVid, selectedPid);
                        Log($"Receiver install failed validation or completed partially for {selectedVariant.Name} VID=0x{selectedVid} PID=0x{selectedPid}; starting exact automatic rollback.");
                        bool rollbackSuccess = await RestoreDriverConfigAsync(
                            scopedConfig,
                            newlyOwnedPackages,
                            deleteManifestOnSuccess: false,
                            operationName: "automatic rollback");
                        Log($"Receiver automatic rollback result={rollbackSuccess} variant={selectedVariant.Name} VID=0x{selectedVid} PID=0x{selectedPid}.");
                        if (!rollbackSuccess)
                        {
                            SaveOwnedDriverPackages(config, newlyOwnedPackages);
                        }
                    }
                    preserveDiagnostics = !success;
                    return success;
                }

                Log($"{config.LogName} install result file not found");
                preserveDiagnostics = true;
                return false;
            }
            catch (Win32Exception ex)
            {
                Log($"Win32Exception (UAC): {ex.NativeErrorCode} - {ex.Message}");
                preserveDiagnostics = true;
                return false;
            }
            catch (Exception ex)
            {
                Log($"Exception: {ex.Message}");
                preserveDiagnostics = true;
                return false;
            }
            finally
            {
                LogFileContents(wdiLog, $"{config.LogName} WDI output");
                if (preserveDiagnostics)
                {
                    Log($"{config.LogName} install diagnostics retained at: {workDir}");
                }
                else
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { }
                }
            }
        }

        private static void AppendObsoleteBindingMigrationScript(
            StringBuilder ps1,
            DriverTargetConfig config,
            string wdiLog)
        {
            ps1.AppendLine("$migrationFailures = 0");
            ps1.AppendLine("$migrationPerformed = $false");

            foreach (var variant in config.Variants)
            {
                foreach (var binding in variant.ObsoleteBindings)
                {
                    string miHex = binding.InterfaceId.ToString("X2");
                    string hardwareId = $"VID_{variant.VidValue}&PID_{binding.PidValue}&MI_{miHex}";
                    string requiredBindingPattern = $"VID_{variant.VidValue}&PID_{binding.PidValue}&MI_(03|04)";
                    ps1.AppendLine($"if ($selectedVidValue -eq {PowerShellLiteral(variant.VidValue)} -and $selectedPidValue -eq {PowerShellLiteral(binding.PidValue)}) {{");
                    ps1.AppendLine($"    $obsoletePattern = {PowerShellLiteral($"USB\\{hardwareId}\\*")}");
                    ps1.AppendLine("    $obsoleteDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like $obsoletePattern })");
                    ps1.AppendLine("    foreach ($obsoleteDevice in $obsoleteDevices) {");
                    ps1.AppendLine("        if ($obsoleteDevice.Service -notmatch 'WINUSB') { continue }");
                    ps1.AppendLine("        $driverInf = (Get-PnpDeviceProperty -InstanceId $obsoleteDevice.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data");
                    ps1.AppendLine("        $infPath = if ($driverInf -match '^oem[0-9]+\\.inf$') { Join-Path (Join-Path $env:windir 'INF') $driverInf } else { $null }");
                    ps1.AppendLine("        $content = if ($infPath -and (Test-Path -LiteralPath $infPath -PathType Leaf)) { Get-Content -LiteralPath $infPath -Raw -ErrorAction SilentlyContinue } else { '' }");
                    ps1.AppendLine($"        $hasExactHardwareId = $content -match [regex]::Escape({PowerShellLiteral(hardwareId)})");
                    ps1.AppendLine("        $isWinUsbPackage = $content -match 'AddService\\s*=\\s*WinUSB'");
                    ps1.AppendLine("        $isManifestOwned = $knownOwnedInfs -contains $driverInf");
                    ps1.AppendLine("        $isZeroCueOwned = $content -match 'ZeroCue'");
                    ps1.AppendLine("        $hasLegacyIdentity = $content -match '(?im)^\\s*DeviceName\\s*=\\s*\"SCUF\"\\s*$' -and $content -match '(?im)^\\s*SourceName\\s*=\\s*\"SCUF Install Disk\"\\s*$' -and $content -match '(?im)^\\s*Provider\\s*=\\s*\"libwdi\"\\s*$'");
                    ps1.AppendLine("        $hasScopedZadigIdentity = $content -match '(?im)^\\s*DeviceName\\s*=\\s*\"SCUF[^\"]+\"\\s*$' -and $content -match '(?im)^\\s*VendorName\\s*=\\s*\"Corsair\"\\s*$' -and $content -match '(?im)^\\s*SourceName\\s*=\\s*\"SCUF[^\"]+ Install Disk\"\\s*$' -and $content -match '(?im)^\\s*Provider\\s*=\\s*\"libwdi\"\\s*$' -and $content -match '(?im)^\\s*Class\\s*=\\s*\"USBDevice\"\\s*$' -and $content -match '(?im)^\\s*DriverVer\\s*=\\s*06/02/2012,\\s*6\\.1\\.7600\\.16385\\s*$'");
                    ps1.AppendLine($"        $containsRequiredReceiverBinding = $content -match {PowerShellLiteral(requiredBindingPattern)}");
                    ps1.AppendLine("        $isVerifiedObsoletePackage = $hasExactHardwareId -and $isWinUsbPackage -and -not $containsRequiredReceiverBinding -and (($isManifestOwned -and $isZeroCueOwned) -or $isZeroCueOwned -or $hasLegacyIdentity -or $hasScopedZadigIdentity)");
                    ps1.AppendLine("        if (-not $isVerifiedObsoletePackage) {");
                    ps1.AppendLine($"            Write-Output \"Refusing to remove unverified obsolete WinUSB binding $($obsoleteDevice.InstanceId) package=$driverInf.\" >> {PowerShellLiteral(wdiLog)}");
                    ps1.AppendLine("            $migrationFailures++");
                    ps1.AppendLine("            continue");
                    ps1.AppendLine("        }");
                    ps1.AppendLine($"        Write-Output \"Repairing obsolete WinUSB binding $($obsoleteDevice.InstanceId) package=$driverInf.\" >> {PowerShellLiteral(wdiLog)}");
                    ps1.AppendLine("        $removeOutput = & pnputil.exe /remove-device $obsoleteDevice.InstanceId 2>&1");
                    ps1.AppendLine("        $removeExitCode = $LASTEXITCODE");
                    ps1.AppendLine($"        $removeOutput | Out-File -FilePath {PowerShellLiteral(wdiLog)} -Append -Encoding utf8");
                    ps1.AppendLine("        if ($removeExitCode -ne 0) { $migrationFailures++; continue }");
                    ps1.AppendLine("        $migrationPerformed = $true");
                    ps1.AppendLine("        $deleteOutput = & pnputil.exe /delete-driver $driverInf /uninstall /force 2>&1");
                    ps1.AppendLine("        $deleteExitCode = $LASTEXITCODE");
                    ps1.AppendLine($"        $deleteOutput | Out-File -FilePath {PowerShellLiteral(wdiLog)} -Append -Encoding utf8");
                    ps1.AppendLine("        if ($deleteExitCode -ne 0) { $migrationFailures++; continue }");
                    ps1.AppendLine("    }");
                    ps1.AppendLine("}");
                }
            }

            ps1.AppendLine("if ($migrationPerformed) {");
            ps1.AppendLine("    pnputil /scan-devices | Out-Null");
            ps1.AppendLine("    Start-Sleep -Seconds 3");
            ps1.AppendLine("}");
        }

        private static void AppendReceiverBindingReenumerationScript(
            StringBuilder ps1,
            DriverTargetConfig config,
            string wdiLog)
        {
            var receiverBindings = config.Variants
                .SelectMany(variant => variant.Bindings.Select(binding => new
                {
                    HardwareId = binding.InterfaceId is int interfaceId
                        ? $"VID_{variant.VidValue}&PID_{binding.PidValue}&MI_{interfaceId:X2}"
                        : $"VID_{variant.VidValue}&PID_{binding.PidValue}",
                    InstancePattern = binding.InterfaceId is int id
                        ? $"USB\\VID_{variant.VidValue}&PID_{binding.PidValue}&MI_{id:X2}\\*"
                        : $"USB\\VID_{variant.VidValue}&PID_{binding.PidValue}\\*",
                    binding.Name
                }))
                .GroupBy(binding => binding.HardwareId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            ps1.AppendLine("$receiverBindings = @(");
            foreach (var binding in receiverBindings)
            {
                ps1.AppendLine($"    [pscustomobject]@{{ HardwareId={PowerShellLiteral(binding.HardwareId)}; InstancePattern={PowerShellLiteral(binding.InstancePattern)}; Name={PowerShellLiteral(binding.Name)} }}");
            }
            ps1.AppendLine(")");

            ps1.AppendLine("$selectedReceiverBindings = @($receiverBindings | Where-Object { $_.HardwareId -like \"VID_$selectedVidValue&PID_$selectedPidValue*\" })");
            ps1.AppendLine("if ($selectedReceiverBindings.Count -gt 0 -and $resultCodes.Count -gt 0 -and $resultCodes -notcontains -1) {");
            ps1.AppendLine($"    Write-Output \"Starting exact receiver binding re-enumeration bindings=$($selectedReceiverBindings.Name -join '|').\" >> {PowerShellLiteral(wdiLog)}");
            ps1.AppendLine("    $selectedBindingDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
            ps1.AppendLine("        $instanceId = $_.InstanceId");
            ps1.AppendLine("        @($selectedReceiverBindings | Where-Object { $instanceId -like $_.InstancePattern }).Count -gt 0");
            ps1.AppendLine("    })");
            ps1.AppendLine("    Write-PnpSnapshot -label 'receiver-before-restart' -devices $selectedBindingDevices");
            ps1.AppendLine("    foreach ($bindingDevice in $selectedBindingDevices | Sort-Object InstanceId) {");
            ps1.AppendLine("        $restartOutput = & pnputil.exe /restart-device $bindingDevice.InstanceId 2>&1");
            ps1.AppendLine("        $restartExitCode = $LASTEXITCODE");
            ps1.AppendLine($"        $restartOutput | Out-File -FilePath {PowerShellLiteral(wdiLog)} -Append -Encoding utf8");
            ps1.AppendLine($"        Write-Output \"PnP restart binding=$($bindingDevice.InstanceId) exitCode=$restartExitCode output=$($restartOutput -join ' | ')\" >> {PowerShellLiteral(wdiLog)}");
            ps1.AppendLine("    }");
            ps1.AppendLine("    $scanOutput = & pnputil.exe /scan-devices 2>&1");
            ps1.AppendLine("    $scanExitCode = $LASTEXITCODE");
            ps1.AppendLine($"    $scanOutput | Out-File -FilePath {PowerShellLiteral(wdiLog)} -Append -Encoding utf8");
            ps1.AppendLine($"    Write-Output \"PnP scan after install exitCode=$scanExitCode\" >> {PowerShellLiteral(wdiLog)}");
            ps1.AppendLine("    $readyBindingCount = 0");
            ps1.AppendLine("    $readyInstances = @()");
            ps1.AppendLine("    $missingBindings = @($selectedReceiverBindings.Name)");
            ps1.AppendLine("    for ($attempt = 1; $attempt -le 15; $attempt++) {");
            ps1.AppendLine("        Start-Sleep -Seconds 1");
            ps1.AppendLine("        $presentPnpDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)");
            ps1.AppendLine("        $bindingStates = @($selectedReceiverBindings | ForEach-Object {");
            ps1.AppendLine("            $binding = $_");
            ps1.AppendLine("            $matchingDevices = @($presentPnpDevices | Where-Object { $_.InstanceId -like $binding.InstancePattern -and $_.Service -match 'WINUSB' })");
            ps1.AppendLine("            [pscustomobject]@{ Name=$binding.Name; Ready=($matchingDevices.Count -gt 0); Instances=@($matchingDevices.InstanceId) }");
            ps1.AppendLine("        })");
            ps1.AppendLine("        $readyBindingCount = @($bindingStates | Where-Object Ready).Count");
            ps1.AppendLine("        $readyInstances = @($bindingStates | Where-Object Ready | ForEach-Object { $_.Instances })");
            ps1.AppendLine("        $missingBindings = @($bindingStates | Where-Object { -not $_.Ready } | Select-Object -ExpandProperty Name)");
            ps1.AppendLine($"        Write-Output \"WinUSB binding readiness attempt=$attempt ready=$readyBindingCount/$($selectedReceiverBindings.Count) missing=$($missingBindings -join '|') instances=$($readyInstances -join '|')\" >> {PowerShellLiteral(wdiLog)}");
            ps1.AppendLine("        if ($readyBindingCount -ge $selectedReceiverBindings.Count) { break }");
            ps1.AppendLine("        if ($attempt -eq 5 -or $attempt -eq 10) {");
            ps1.AppendLine("            $retryScanOutput = & pnputil.exe /scan-devices 2>&1");
            ps1.AppendLine("            $retryScanExitCode = $LASTEXITCODE");
            ps1.AppendLine($"            Write-Output \"PnP readiness rescan attempt=$attempt exitCode=$retryScanExitCode output=$($retryScanOutput -join ' | ')\" >> {PowerShellLiteral(wdiLog)}");
            ps1.AppendLine("        }");
            ps1.AppendLine("    }");
            ps1.AppendLine("    $finalBindingDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
            ps1.AppendLine("        $instanceId = $_.InstanceId");
            ps1.AppendLine("        @($selectedReceiverBindings | Where-Object { $instanceId -like $_.InstancePattern }).Count -gt 0");
            ps1.AppendLine("    })");
            ps1.AppendLine("    Write-PnpSnapshot -label 'receiver-after-restart' -devices $finalBindingDevices");
            ps1.AppendLine("    if ($readyBindingCount -lt $selectedReceiverBindings.Count) {");
            ps1.AppendLine($"        Write-Output \"Receiver binding re-enumeration failed missing=$($missingBindings -join '|').\" >> {PowerShellLiteral(wdiLog)}");
            ps1.AppendLine("        $resultCodes += -1");
            ps1.AppendLine("    }");
            ps1.AppendLine("}");
        }

        private async Task<bool> ValidateReceiverWinUsbTopologyAsync(
            string vidValue,
            string pidValue,
            DriverHardwareVariant driverVariant)
        {
            if (!int.TryParse(vidValue, System.Globalization.NumberStyles.HexNumber, null, out var vid) ||
                !int.TryParse(pidValue, System.Globalization.NumberStyles.HexNumber, null, out var pid))
            {
                Log($"Receiver driver validation failed: invalid selected identity VID=0x{vidValue} PID=0x{pidValue}.");
                return false;
            }

            var receiverIdentity = SupportedScufDeviceProfile.ScufEnvisionPro.FindWirelessReceiver(vid, pid);
            if (receiverIdentity == null)
            {
                Log($"Receiver driver validation failed: selected identity VID=0x{vid:X4} PID=0x{pid:X4} is not in the runtime receiver profile.");
                return false;
            }

            const int maxAttempts = 20;
            Log($"Receiver topology validation start variant={driverVariant.Name} runtimeVariant={receiverIdentity.Variant} experimental={receiverIdentity.IsExperimental} VID=0x{vid:X4} PID=0x{pid:X4} attempts={maxAttempts} delayMs=1000.");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                bool runtimeReady = false;
                bool radioReady = false;
                Log($"Receiver topology validation attempt={attempt}/{maxAttempts}.");

                try
                {
                    using (var runtimeTransport = new WirelessDongleWinUsbTransport(
                        message => Log($"receiver validation: {message}"),
                        WirelessWinUsbInterfaceTarget.RuntimeMi04,
                        logReadPayloads: false,
                        receiverIdentity: receiverIdentity))
                    {
                        using var runtimeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        runtimeReady = await runtimeTransport.ConnectAsync(runtimeTimeout.Token);
                        await runtimeTransport.DisconnectAsync();
                    }

                    if (runtimeReady)
                    {
                        using var radioTransport = new WirelessDongleWinUsbTransport(
                            message => Log($"receiver validation: {message}"),
                            WirelessWinUsbInterfaceTarget.RadioMi03,
                            logReadPayloads: false,
                            receiverIdentity: receiverIdentity);
                        using var radioTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        radioReady = await radioTransport.ConnectAsync(radioTimeout.Token);
                        await radioTransport.DisconnectAsync();
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException || ex is OperationCanceledException)
                {
                    Log($"Receiver topology validation attempt={attempt} transient failure: {ex.Message}");
                }

                Log($"Receiver topology validation attempt={attempt} runtimeMi04={runtimeReady} radioMi03={radioReady}.");
                if (runtimeReady && radioReady)
                {
                    Log($"Receiver driver validation succeeded variant={receiverIdentity.Variant} experimental={receiverIdentity.IsExperimental} VID=0x{vid:X4} PID=0x{pid:X4} for MI_04 control and MI_03 input pipes.");
                    return true;
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(1000);
                }
            }

            Log("Receiver driver validation failed after re-enumeration retries: MI_04 must expose 64-byte OUT 0x02 / IN 0x82 pipes and MI_03 must expose a 64-byte IN 0x81 pipe through WinUSB.");
            return false;
        }

        private Task<bool> RestoreDefaultDriversAsync(DriverTarget target)
        {
            var config = DriverTargetConfig.For(target);
            var ownedPackages = LoadOwnedDriverPackages(config);
            Log($"Loaded {ownedPackages.Count} owned driver package(s) for {config.LogName} restore; elevated restore will also discover strictly scoped compatible WinUSB packages.");
            return RestoreDriverConfigAsync(
                config,
                ownedPackages,
                deleteManifestOnSuccess: true,
                operationName: "restore");
        }

        private async Task<bool> RestoreDriverConfigAsync(
            DriverTargetConfig config,
            IReadOnlyList<string> ownedPackages,
            bool deleteManifestOnSuccess,
            string operationName)
        {
            string workDir = Path.Combine(Path.GetTempPath(), "ZeroCue", "driver", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            string ps1Path = Path.Combine(workDir, $"zerocue_restore_{config.LogName}_drivers.ps1");
            string resultPath = Path.Combine(workDir, $"zerocue_restore_{config.LogName}_result.txt");
            string restoreLogPath = Path.Combine(workDir, $"zerocue_restore_{config.LogName}_output.txt");

            try
            {
                if (File.Exists(resultPath))
                    File.Delete(resultPath);

                string ps1Content = BuildRestoreDriverScript(config, ownedPackages, resultPath, restoreLogPath);

                File.WriteAllText(ps1Path, ps1Content);
                Log($"Created {config.LogName} {operationName} script at: {ps1Path}; identities={string.Join(',', config.HardwareIdentityValues)} packages={string.Join(',', ownedPackages)}");
                if (!ValidatePowerShellScriptSyntax(ps1Path, $"{config.LogName} {operationName}"))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(ps1Path);

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    Log($"{config.LogName} {operationName} script ExitCode: {process.ExitCode}");
                }

                if (File.Exists(resultPath))
                {
                    var result = ParseKeyValueResult(resultPath);
                    bool success = result.TryGetValue("success", out var successValue) &&
                        bool.TryParse(successValue, out var parsedSuccess) &&
                        parsedSuccess;
                    Log($"{config.LogName} {operationName} results: {string.Join("; ", result.Select(pair => $"{pair.Key}={pair.Value}"))}");
                    if (success && deleteManifestOnSuccess)
                    {
                        DeleteOwnedDriverPackageManifest(config);
                    }

                    return success;
                }

                Log($"{config.LogName} {operationName} result file not found");
                return false;
            }
            catch (Win32Exception ex)
            {
                Log($"Win32Exception (UAC): {ex.NativeErrorCode} - {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Exception: {ex.Message}");
                return false;
            }
            finally
            {
                LogFileContents(restoreLogPath, $"{config.LogName} {operationName} output");
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        private static string BuildRestoreDriverScript(
            DriverTargetConfig config,
            IReadOnlyList<string> ownedPackages,
            string resultPath,
            string restoreLogPath)
        {
            var ownedInfArray = string.Join(",", ownedPackages.Select(PowerShellLiteral));
            var targetHardwareIdArray = string.Join(",", config.HardwareIdentityValues.Select(PowerShellLiteral));
            var legacyHardwareIdArray = string.Join(",", config.Variants
                .SelectMany(variant => variant.Bindings
                    .Select(binding => binding.InterfaceId is int interfaceId
                        ? $"VID_{variant.VidValue}&PID_{binding.PidValue}&MI_{interfaceId:X2}"
                        : $"VID_{variant.VidValue}&PID_{binding.PidValue}")
                    .Concat(variant.ObsoleteBindings.Select(binding =>
                        $"VID_{variant.VidValue}&PID_{binding.PidValue}&MI_{binding.InterfaceId:X2}")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(PowerShellLiteral));
            var restorableInterfaceHardwareIdArray = string.Join(",", config.Variants
                .SelectMany(variant => variant.Bindings
                    .Where(binding => binding.InterfaceId.HasValue)
                    .Select(binding =>
                        $"VID_{variant.VidValue}&PID_{binding.PidValue}&MI_{binding.InterfaceId!.Value:X2}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(PowerShellLiteral));
            return
                $"try {{ Start-Transcript -LiteralPath {PowerShellLiteral(restoreLogPath)} -Force | Out-Null }} catch {{ Write-Warning \"Could not start restore transcript: $($_.Exception.Message)\" }}\r\n" +
                BuildCorsairShutdownScript() +
                "Start-Sleep -Seconds 3\r\n" +
                $"$ownedInfs = @({ownedInfArray})\r\n" +
                $"$legacyHardwareIds = @({legacyHardwareIdArray})\r\n" +
                $"$restorableInterfaceHardwareIds = @({restorableInterfaceHardwareIdArray})\r\n" +
                $"$targetHardwareIds = @({targetHardwareIdArray})\r\n" +
                "$verifiedInfs = @()\r\n" +
                "$removedCount = 0\r\n" +
                "$removedDeviceCount = 0\r\n" +
                "$missingCount = 0\r\n" +
                "$failureCount = 0\r\n" +
                "$warningCount = 0\r\n" +
                "$legacyPackageCount = 0\r\n" +
                "$targetDevices = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {\r\n" +
                "    $instanceId = $_.InstanceId\r\n" +
                "    $hasTargetId = $false\r\n" +
                "    foreach ($targetHardwareId in $targetHardwareIds) { if ($instanceId -match [regex]::Escape($targetHardwareId)) { $hasTargetId = $true; break } }\r\n" +
                "    $hasTargetId\r\n" +
                "})\r\n" +
                "$presentTargetDevicesBefore = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {\r\n" +
                "    $instanceId = $_.InstanceId\r\n" +
                "    $hasTargetId = $false\r\n" +
                "    foreach ($targetHardwareId in $targetHardwareIds) { if ($instanceId -match [regex]::Escape($targetHardwareId)) { $hasTargetId = $true; break } }\r\n" +
                "    $hasTargetId\r\n" +
                "})\r\n" +
                "$expectedPresentInterfaceHardwareIds = @($restorableInterfaceHardwareIds | Where-Object {\r\n" +
                "    $hardwareId = $_\r\n" +
                "    @($presentTargetDevicesBefore | Where-Object { $_.InstanceId -match [regex]::Escape($hardwareId) }).Count -gt 0\r\n" +
                "})\r\n" +
                "$compositeParentInstanceIds = @($presentTargetDevicesBefore | Where-Object {\r\n" +
                "    $_.InstanceId -notmatch '&MI_[0-9A-F]{2}' -and $_.Service -match 'usbccgp'\r\n" +
                "} | Select-Object -ExpandProperty InstanceId -Unique)\r\n" +
                "$initialState = @($targetDevices | Sort-Object InstanceId | ForEach-Object {\r\n" +
                "    $driverInf = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data\r\n" +
                "    $parent = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data\r\n" +
                "    $problemCode = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data\r\n" +
                "    $hardwareIds = ((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data -join ';')\r\n" +
                "    \"instance=$($_.InstanceId);friendlyName=$($_.FriendlyName);status=$($_.Status);class=$($_.Class);service=$($_.Service);driverInf=$driverInf;problemCode=$problemCode;parent=$parent;hardwareIds=$hardwareIds\"\r\n" +
                "})\r\n" +
                "$initialWinUsbDevices = @($presentTargetDevicesBefore | Where-Object { $_.Service -match 'WINUSB' })\r\n" +
                "$legacyCandidates = @(Get-ChildItem -Path (Join-Path $env:windir 'INF\\oem*.inf') -File -ErrorAction SilentlyContinue)\r\n" +
                "foreach ($infFile in $legacyCandidates) {\r\n" +
                "    $content = Get-Content -LiteralPath $infFile.FullName -Raw -ErrorAction SilentlyContinue\r\n" +
                "    $hasExactHardwareId = $false\r\n" +
                "    foreach ($hardwareId in $legacyHardwareIds) { if ($content -match [regex]::Escape($hardwareId)) { $hasExactHardwareId = $true; break } }\r\n" +
                "    $hasLegacyIdentity = $content -match '(?im)^\\s*DeviceName\\s*=\\s*\"SCUF\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*SourceName\\s*=\\s*\"SCUF Install Disk\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*Provider\\s*=\\s*\"libwdi\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*Class\\s*=\\s*\"USBDevice\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*CatalogFile\\s*=\\s*usb_device\\.cat\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*DriverVer\\s*=\\s*04/18/2019,\\s*6\\.1\\.7600\\.16385\\s*$'\r\n" +
                "    $hasScopedZadigIdentity = $content -match '(?im)^\\s*DeviceName\\s*=\\s*\"SCUF[^\"]+\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*VendorName\\s*=\\s*\"Corsair\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*SourceName\\s*=\\s*\"SCUF[^\"]+ Install Disk\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*Provider\\s*=\\s*\"libwdi\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*Class\\s*=\\s*\"USBDevice\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*DriverVer\\s*=\\s*06/02/2012,\\s*6\\.1\\.7600\\.16385\\s*$'\r\n" +
                "    $hasOrphanedZeroCueIdentity = $content -match 'ZeroCue' -and\r\n" +
                "        $content -match '(?im)^\\s*Provider\\s*=\\s*\"libwdi\"\\s*$' -and\r\n" +
                "        $content -match '(?im)^\\s*Class\\s*=\\s*\"USBDevice\"\\s*$'\r\n" +
                "    $isLegacyZeroCueWinUsb = $content -match 'WinUSBDeviceClassReg' -and $content -match 'AddService\\s*=\\s*WinUSB'\r\n" +
                "    if ($hasExactHardwareId -and $isLegacyZeroCueWinUsb -and ($hasLegacyIdentity -or $hasScopedZadigIdentity -or $hasOrphanedZeroCueIdentity)) {\r\n" +
                "        $verifiedInfs += $infFile.Name\r\n" +
                "    }\r\n" +
                "}\r\n" +
                "$verifiedInfs = @($verifiedInfs | Sort-Object -Unique)\r\n" +
                "$legacyPackageCount = $verifiedInfs.Count\r\n" +
                "foreach ($infName in $ownedInfs) {\r\n" +
                "    $infPath = Join-Path (Join-Path $env:windir 'INF') $infName\r\n" +
                "    if (-not (Test-Path -LiteralPath $infPath -PathType Leaf)) { $missingCount++; continue }\r\n" +
                "    $content = Get-Content -LiteralPath $infPath -Raw -ErrorAction SilentlyContinue\r\n" +
                "    $hasTargetId = $false\r\n" +
                "    foreach ($targetHardwareId in $targetHardwareIds) { if ($content -match [regex]::Escape($targetHardwareId)) { $hasTargetId = $true; break } }\r\n" +
                "    if ($content -notmatch 'ZeroCue' -or -not $hasTargetId) {\r\n" +
                "        Write-Warning \"Refusing unverified driver package $infName\"\r\n" +
                "        $failureCount++\r\n" +
                "        continue\r\n" +
                "    }\r\n" +
                "    $verifiedInfs += $infName\r\n" +
                "}\r\n" +
                "$verifiedInfs = @($verifiedInfs | Sort-Object -Unique)\r\n" +
                "if ($verifiedInfs.Count -eq 0 -and $initialWinUsbDevices.Count -gt 0) {\r\n" +
                "    Write-Warning 'WinUSB is active but no verified compatible driver package was found for this device.'\r\n" +
                "    $failureCount++\r\n" +
                "}\r\n" +
                "$ownedDevices = @($targetDevices | ForEach-Object {\r\n" +
                "    $device = $_\r\n" +
                "    $driverInf = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data\r\n" +
                "    if ($verifiedInfs -contains $driverInf) { $device }\r\n" +
                "})\r\n" +
                "$ownedDeviceInstanceIds = @($ownedDevices | Select-Object -ExpandProperty InstanceId -Unique)\r\n" +
                "foreach ($ownedDeviceInstanceId in @($ownedDeviceInstanceIds | Sort-Object { $_.Length } -Descending)) {\r\n" +
                "    $currentOwnedDevice = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -eq $ownedDeviceInstanceId })\r\n" +
                "    if ($currentOwnedDevice.Count -eq 0) {\r\n" +
                "        Write-Warning \"Driver interface already re-enumerated before removal: $ownedDeviceInstanceId\"\r\n" +
                "        $warningCount++\r\n" +
                "        continue\r\n" +
                "    }\r\n" +
                "    $removeDeviceOutput = & pnputil /remove-device $ownedDeviceInstanceId 2>&1\r\n" +
                "    $removeDeviceExitCode = $LASTEXITCODE\r\n" +
                "    $removeDeviceOutput | Write-Output\r\n" +
                "    if ($removeDeviceExitCode -eq 0) {\r\n" +
                "        $removedDeviceCount++\r\n" +
                "    } else {\r\n" +
                "        $deviceAfterFailedRemoval = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -eq $ownedDeviceInstanceId })\r\n" +
                "        if ($deviceAfterFailedRemoval.Count -eq 0 -or $deviceAfterFailedRemoval.Service -notmatch 'WINUSB') {\r\n" +
                "            Write-Warning \"Driver interface changed during removal and is no longer on WinUSB: $ownedDeviceInstanceId\"\r\n" +
                "            $warningCount++\r\n" +
                "        } else {\r\n" +
                "            $failureCount++\r\n" +
                "        }\r\n" +
                "    }\r\n" +
                "}\r\n" +
                "foreach ($infName in $verifiedInfs) {\r\n" +
                "    $deleteDriverOutput = & pnputil /delete-driver $infName /uninstall /force 2>&1\r\n" +
                "    $deleteDriverExitCode = $LASTEXITCODE\r\n" +
                "    $deleteDriverOutput | Write-Output\r\n" +
                "    $infPathAfterDelete = Join-Path (Join-Path $env:windir 'INF') $infName\r\n" +
                "    if ($deleteDriverExitCode -eq 0 -or -not (Test-Path -LiteralPath $infPathAfterDelete -PathType Leaf)) {\r\n" +
                "        $removedCount++\r\n" +
                "        if ($deleteDriverExitCode -ne 0) { $warningCount++ }\r\n" +
                "    } else {\r\n" +
                "        $failureCount++\r\n" +
                "    }\r\n" +
                "}\r\n" +
                "pnputil /scan-devices | Out-Null\r\n" +
                "foreach ($compositeParentInstanceId in $compositeParentInstanceIds) {\r\n" +
                "    $restartParentOutput = & pnputil.exe /restart-device $compositeParentInstanceId 2>&1\r\n" +
                "    $restartParentExitCode = $LASTEXITCODE\r\n" +
                "    $restartParentOutput | Write-Output\r\n" +
                "    if ($restartParentExitCode -ne 0) { $warningCount++ }\r\n" +
                "}\r\n" +
                "pnputil /scan-devices | Out-Null\r\n" +
                "$remainingWinUsbDevices = @()\r\n" +
                "$restoredInterfaceHardwareIds = @()\r\n" +
                "$missingInterfaceHardwareIds = @($expectedPresentInterfaceHardwareIds)\r\n" +
                "for ($attempt = 1; $attempt -le 20; $attempt++) {\r\n" +
                "    Start-Sleep -Seconds 1\r\n" +
                "    $presentTargetDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {\r\n" +
                "        $instanceId = $_.InstanceId\r\n" +
                "        $hasTargetId = $false\r\n" +
                "        foreach ($targetHardwareId in $targetHardwareIds) { if ($instanceId -match [regex]::Escape($targetHardwareId)) { $hasTargetId = $true; break } }\r\n" +
                "        $hasTargetId\r\n" +
                "    })\r\n" +
                "    $remainingWinUsbDevices = @($presentTargetDevices | Where-Object { $_.Service -match 'WINUSB' })\r\n" +
                "    $restoredInterfaceHardwareIds = @($expectedPresentInterfaceHardwareIds | Where-Object {\r\n" +
                "        $hardwareId = $_\r\n" +
                "        @($presentTargetDevices | Where-Object { $_.InstanceId -match [regex]::Escape($hardwareId) -and $_.Service -notmatch 'WINUSB' }).Count -gt 0\r\n" +
                "    })\r\n" +
                "    $missingInterfaceHardwareIds = @($expectedPresentInterfaceHardwareIds | Where-Object { $restoredInterfaceHardwareIds -notcontains $_ })\r\n" +
                "    Write-Output \"Driver restore readiness attempt=$attempt remainingWinUsb=$($remainingWinUsbDevices.Count) restoredInterfaces=$($restoredInterfaceHardwareIds.Count)/$($expectedPresentInterfaceHardwareIds.Count) missing=$($missingInterfaceHardwareIds -join ',')\"\r\n" +
                "    if ($remainingWinUsbDevices.Count -eq 0 -and $missingInterfaceHardwareIds.Count -eq 0) { break }\r\n" +
                "    if ($attempt -eq 5 -or $attempt -eq 10 -or $attempt -eq 15) {\r\n" +
                "        foreach ($compositeParentInstanceId in $compositeParentInstanceIds) { & pnputil.exe /restart-device $compositeParentInstanceId | Out-Null }\r\n" +
                "        pnputil /scan-devices | Out-Null\r\n" +
                "    }\r\n" +
                "}\r\n" +
                "if ($remainingWinUsbDevices.Count -gt 0) {\r\n" +
                "    Write-Warning \"WinUSB is still bound to: $($remainingWinUsbDevices.InstanceId -join ', ')\"\r\n" +
                "    $failureCount += $remainingWinUsbDevices.Count\r\n" +
                "}\r\n" +
                "if ($missingInterfaceHardwareIds.Count -gt 0) {\r\n" +
                "    Write-Warning \"Original driver interfaces did not return: $($missingInterfaceHardwareIds -join ', ')\"\r\n" +
                "    $failureCount += $missingInterfaceHardwareIds.Count\r\n" +
                "}\r\n" +
                "$finalDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {\r\n" +
                "    $instanceId = $_.InstanceId\r\n" +
                "    $hasTargetId = $false\r\n" +
                "    foreach ($targetHardwareId in $targetHardwareIds) { if ($instanceId -match [regex]::Escape($targetHardwareId)) { $hasTargetId = $true; break } }\r\n" +
                "    $hasTargetId\r\n" +
                "})\r\n" +
                "$finalState = @($finalDevices | Sort-Object InstanceId | ForEach-Object {\r\n" +
                "    $driverInf = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data\r\n" +
                "    $parent = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data\r\n" +
                "    $problemCode = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data\r\n" +
                "    $hardwareIds = ((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data -join ';')\r\n" +
                "    \"instance=$($_.InstanceId);friendlyName=$($_.FriendlyName);status=$($_.Status);class=$($_.Class);service=$($_.Service);driverInf=$driverInf;problemCode=$problemCode;parent=$parent;hardwareIds=$hardwareIds\"\r\n" +
                "})\r\n" +
                "Stop-Process -Name 'wdi-simple' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Process -Name 'installer_x64' -Force -ErrorAction SilentlyContinue\r\n" +
                BuildCorsairRestartScript() +
                $"Write-Output \"success=$($failureCount -eq 0)\" > {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"removed=$removedCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"devices=$removedDeviceCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"legacy=$legacyPackageCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"missing=$missingCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"failures=$failureCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"warnings=$warningCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"packages=$($verifiedInfs -join ',')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"remaining=$($remainingWinUsbDevices.InstanceId -join '|')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"expectedInterfaces=$($expectedPresentInterfaceHardwareIds -join '|')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"restoredInterfaces=$($restoredInterfaceHardwareIds -join '|')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"missingInterfaces=$($missingInterfaceHardwareIds -join '|')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"before=$($initialState -join '|')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"after=$($finalState -join '|')\" >> {PowerShellLiteral(resultPath)}\r\n" +
                "try { Stop-Transcript | Out-Null } catch { }\r\n";
        }

        private static Dictionary<string, string> ParseKeyValueResult(string path)
        {
            return File.ReadAllLines(path)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                .ToDictionary(
                    parts => parts[0].Trim(),
                    parts => parts[1].Trim(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string[] GetCsvValues(IReadOnlyDictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out var csv) || string.IsNullOrWhiteSpace(csv))
            {
                return Array.Empty<string>();
            }

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static string PowerShellLiteral(string value)
        {
            return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        private static bool IsPublishedInfName(string value)
        {
            return value.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
                value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value[3..^4], out _);
        }

        private bool ValidatePowerShellScriptSyntax(string scriptPath, string operationName)
        {
            string parserCommand =
                "$tokens=$null; $errors=$null; " +
                $"[System.Management.Automation.Language.Parser]::ParseFile({PowerShellLiteral(scriptPath)},[ref]$tokens,[ref]$errors) | Out-Null; " +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(parserCommand);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Log($"PowerShell syntax validation could not start for {operationName}.");
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    Log($"PowerShell syntax validation timed out for {operationName}.");
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    Log($"PowerShell syntax validation failed for {operationName}: {error.Trim()} {output.Trim()}".TrimEnd());
                    return false;
                }

                Log($"PowerShell syntax validation succeeded for {operationName}.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"PowerShell syntax validation failed to run for {operationName}: {ex.Message}");
                return false;
            }
        }

        private string GetDriverPackageManifestPath(DriverTargetConfig config)
        {
            return Path.Combine(ZeroCuePaths.UserDataRoot, "driver-manifests", $"{config.LogName}.json");
        }

        private IReadOnlyList<string> LoadOwnedDriverPackages(DriverTargetConfig config)
        {
            string manifestPath = GetDriverPackageManifestPath(config);
            if (!File.Exists(manifestPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<DriverPackageManifest>(File.ReadAllText(manifestPath));
                var expectedHardwareIds = config.HardwareIdentityValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var manifestHardwareIds = manifest?.HardwareIds?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if ((manifestHardwareIds == null || manifestHardwareIds.Count == 0) &&
                    manifest != null &&
                    !string.IsNullOrWhiteSpace(manifest.VidValue))
                {
                    manifestHardwareIds = manifest.PidValues
                        .Select(pid => $"VID_{manifest.VidValue}&PID_{pid}")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                bool identityMatches = manifest != null &&
                    string.Equals(manifest.Target, config.LogName, StringComparison.Ordinal) &&
                    manifestHardwareIds != null &&
                    manifestHardwareIds.Count > 0 &&
                    manifestHardwareIds.IsSubsetOf(expectedHardwareIds);

                if (!identityMatches)
                {
                    Log($"Ignoring invalid driver package manifest for {config.LogName}.");
                    return Array.Empty<string>();
                }

                return manifest!.PublishedInfNames
                    .Where(IsPublishedInfName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Log($"Could not read driver package manifest for {config.LogName}: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private void SaveOwnedDriverPackages(DriverTargetConfig config, IEnumerable<string> newlyOwnedPackages)
        {
            string windowsInfDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "INF");
            var allOwnedPackages = LoadOwnedDriverPackages(config)
                .Concat(newlyOwnedPackages.Where(IsPublishedInfName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(package => File.Exists(Path.Combine(windowsInfDirectory, package)))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (allOwnedPackages.Length == 0)
            {
                DeleteOwnedDriverPackageManifest(config);
                return;
            }

            string manifestPath = GetDriverPackageManifestPath(config);
            string manifestDirectory = Path.GetDirectoryName(manifestPath)!;
            Directory.CreateDirectory(manifestDirectory);

            var manifest = new DriverPackageManifest
            {
                Target = config.LogName,
                HardwareIds = config.HardwareIdentityValues,
                VidValue = config.Variants.Length == 1 ? config.Variants[0].VidValue : string.Empty,
                PidValues = config.Variants.SelectMany(variant => variant.PidValues).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                PublishedInfNames = allOwnedPackages
            };
            var options = new JsonSerializerOptions { WriteIndented = true };
            AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        }

        private void DeleteOwnedDriverPackageManifest(DriverTargetConfig config)
        {
            try
            {
                string manifestPath = GetDriverPackageManifestPath(config);
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            catch (Exception ex)
            {
                Log($"Could not delete driver package manifest for {config.LogName}: {ex.Message}");
            }
        }

        private static string BuildCorsairShutdownScript()
        {
            return
                "Stop-Process -Name 'wdi-simple' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Process -Name 'installer_x64' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Process -Name 'iCUE' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Process -Name 'iCUEDevicePluginHost' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Service -Name 'CorsairDeviceControlService' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Service -Name 'CorsairService' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Service -Name 'CorsairGamingAudioCfgService64' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Service -Name 'CorsairCpuIdService' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Service -Name 'iCUEUpdateService' -Force -ErrorAction SilentlyContinue\r\n";
        }

        private static string BuildCorsairRestartScript()
        {
            return
                "$corsairServices = @('CorsairDeviceControlService','CorsairService','CorsairGamingAudioCfgService64','CorsairCpuIdService','iCUEUpdateService')\r\n" +
                "foreach ($serviceName in $corsairServices) { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }\r\n" +
                "Start-Sleep -Seconds 2\r\n" +
                "$icueCandidates = @(\r\n" +
                "    (Join-Path $env:ProgramFiles 'Corsair\\CORSAIR iCUE5 Software\\iCUE.exe'),\r\n" +
                "    (Join-Path $env:ProgramFiles 'Corsair\\CORSAIR iCUE4 Software\\iCUE.exe'),\r\n" +
                "    (Join-Path ${env:ProgramFiles(x86)} 'Corsair\\CORSAIR iCUE Software\\iCUE.exe')\r\n" +
                ")\r\n" +
                "$icuePath = $icueCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1\r\n" +
                "if ($icuePath -and -not (Get-Process -Name 'iCUE' -ErrorAction SilentlyContinue)) { Start-Process -FilePath $icuePath }\r\n";
        }

        private void LogFileContents(string path, string label)
        {
            try
            {
                if (File.Exists(path))
                {
                    Log($"{label}:{Environment.NewLine}{File.ReadAllText(path)}");
                }
            }
            catch (Exception ex)
            {
                Log($"Could not read {label}: {ex.Message}");
            }
        }

        public async Task<string> GetWirelessReceiverPnpInstancesAsync()
        {
            try
            {
                string noInstancesMessage = PowerShellLiteral(LocalizationService.Get("WirelessReceiverInstancesNotFound"));
                string script =
                    "$devices = Get-PnpDevice | Where-Object {\r\n" +
                    "    ($_.InstanceId -match 'VID_1B1C&PID_3A08') -or\r\n" +
                    "    ($_.InstanceId -match 'VID_1B1C&PID_3A09') -or\r\n" +
                    "    ($_.InstanceId -match 'VID_2E95&PID_434E') -or\r\n" +
                    "    ($_.InstanceId -match 'VID_2E95&PID_5046')\r\n" +
                    "} | Where-Object {\r\n" +
                    "    $_.InstanceId -notmatch 'PID_3A05'\r\n" +
                    "} | ForEach-Object {\r\n" +
                    "    $driverInf = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data\r\n" +
                    "    [pscustomobject]@{ InstanceId=$_.InstanceId; Status=$_.Status; Class=$_.Class; Service=$_.Service; DriverInf=$driverInf; FriendlyName=$_.FriendlyName }\r\n" +
                    "}\r\n" +
                    "if ($devices) {\r\n" +
                    "    $devices | Format-Table -AutoSize | Out-String\r\n" +
                    "} else {\r\n" +
                    $"    Write-Output {noInstancesMessage}\r\n" +
                    "}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);

                using var process = Process.Start(psi);
                if (process != null)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return string.Format(
                            LocalizationService.Get("PowerShellExecutionErrorFormat"),
                            output.TrimEnd(),
                            error.TrimEnd());
                    }
                    return output;
                }
                return LocalizationService.Get("PowerShellLaunchFailed");
            }
            catch (Exception ex)
            {
                return string.Format(LocalizationService.Get("PowerShellExceptionFormat"), ex.Message);
            }
        }

        private sealed class DriverPackageManifest
        {
            public string Target { get; init; } = string.Empty;
            public string VidValue { get; init; } = string.Empty;
            public string[] PidValues { get; init; } = Array.Empty<string>();
            public string[] HardwareIds { get; init; } = Array.Empty<string>();
            public string[] PublishedInfNames { get; init; } = Array.Empty<string>();
        }

        private enum DriverTarget
        {
            Gamepad,
            Receiver
        }

        private sealed record DriverBinding(string Pid, int? InterfaceId, string Name)
        {
            public string PidValue => Pid.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record ObsoleteDriverBinding(string Pid, int InterfaceId, string Name)
        {
            public string PidValue => Pid.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record DriverHardwareVariant(
            string Key,
            string Name,
            string DeviceName,
            string Vid,
            bool IsExperimental,
            DriverBinding[] Bindings,
            ObsoleteDriverBinding[] ObsoleteBindings)
        {
            public string VidValue => Vid.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            public string[] PidValues => Bindings
                .Select(binding => binding.PidValue)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            public DriverHardwareVariant ScopeToPid(string pidValue) => this with
            {
                Bindings = Bindings
                    .Where(binding => string.Equals(binding.PidValue, pidValue, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                ObsoleteBindings = ObsoleteBindings
                    .Where(binding => string.Equals(binding.PidValue, pidValue, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
        }

        private sealed record DriverTargetConfig(
            string LogName,
            string DisplayName,
            DriverHardwareVariant[] Variants)
        {
            public string[] HardwareIdentityValues => Variants
                .SelectMany(variant => variant.PidValues.Select(pid => $"VID_{variant.VidValue}&PID_{pid}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            public DriverTargetConfig ScopeToIdentity(string vidValue, string pidValue) => this with
            {
                Variants = Variants
                    .Where(variant => string.Equals(variant.VidValue, vidValue, StringComparison.OrdinalIgnoreCase))
                    .Select(variant => variant.ScopeToPid(pidValue))
                    .Where(variant => variant.Bindings.Length > 0)
                    .ToArray()
            };

            public DriverHardwareVariant? FindVariant(string vidValue, string pidValue) =>
                Variants.FirstOrDefault(variant =>
                    string.Equals(variant.VidValue, vidValue, StringComparison.OrdinalIgnoreCase) &&
                    variant.PidValues.Contains(pidValue, StringComparer.OrdinalIgnoreCase));

            public static DriverTargetConfig For(DriverTarget target)
            {
                return target switch
                {
                    DriverTarget.Receiver => new DriverTargetConfig(
                        "receiver",
                        "SCUF RF receiver",
                        new[]
                        {
                            new DriverHardwareVariant(
                                "v2",
                                "Envision Pro Wireless USB Receiver V2",
                                "ZeroCue SCUF Envision Pro Receiver V2",
                                "0x1B1C",
                                false,
                                new[]
                                {
                                    new DriverBinding("0x3A08", 4, "V2 interface 4 (MI_04)"),
                                    new DriverBinding("0x3A08", 3, "V2 interface 3 (MI_03)"),
                                    new DriverBinding("0x3A09", null, "V2 active receiver device")
                                },
                                new[]
                                {
                                    new ObsoleteDriverBinding("0x3A08", 0, "obsolete V2 interface 0 (MI_00)")
                                }),
                            new DriverHardwareVariant(
                                "v1",
                                "SCUF PC Controller Dongle V1",
                                "ZeroCue SCUF PC Controller Dongle V1",
                                "0x2E95",
                                true,
                                new[]
                                {
                                    new DriverBinding("0x434E", 4, "experimental V1 interface 4 (MI_04)"),
                                    new DriverBinding("0x434E", 3, "experimental V1 interface 3 (MI_03)"),
                                    new DriverBinding("0x5046", null, "experimental V1 active receiver device")
                                },
                                Array.Empty<ObsoleteDriverBinding>())
                        }),
                    _ => new DriverTargetConfig(
                        "gamepad",
                        "SCUF Envision wired controller",
                        new[]
                        {
                            new DriverHardwareVariant(
                                "wired-v2",
                                "SCUF Envision wired controller",
                                "ZeroCue SCUF Envision Wired",
                                "0x1B1C",
                                false,
                                new[]
                                {
                                    new DriverBinding("0x3A05", 0, "interface 0 (MI_00)"),
                                    new DriverBinding("0x3A05", 4, "interface 4 (MI_04)"),
                                    new DriverBinding("0x3A05", 3, "interface 3 (MI_03)"),
                                    new DriverBinding("0x3A04", 0, "interface 0 (MI_00)"),
                                    new DriverBinding("0x3A04", 4, "interface 4 (MI_04)"),
                                    new DriverBinding("0x3A04", 3, "interface 3 (MI_03)")
                                },
                                Array.Empty<ObsoleteDriverBinding>())
                        })
                };
            }
        }
    }
}
