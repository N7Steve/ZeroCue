using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

            try
            {
                if (File.Exists(resultPath))
                    File.Delete(resultPath);

                string runId = DateTime.Now.ToString("yyyyMMddHHmmss");
                string tempDirPrefix = Path.Combine(workDir, $"zerocue_wdi_{config.LogName}_{runId}");
                string targetPidValues = string.Join(",", config.PidValues.Select(PowerShellLiteral));

                var ps1 = new StringBuilder();
                ps1.AppendLine($"Write-Output '=== WDI Install Log {config.DisplayName} ({DateTime.Now:yyyyMMddHHmmss}) ===' > {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("function Get-PublishedInfNames {");
                ps1.AppendLine("    @(Get-ChildItem -Path (Join-Path $env:windir 'INF\\oem*.inf') -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)");
                ps1.AppendLine("}");
                ps1.AppendLine("$publishedInfsBefore = @(Get-PublishedInfNames)");
                ps1.AppendLine($"$targetPidValues = @({targetPidValues})");
                ps1.AppendLine("$selectedPidValue = $null");
                ps1.AppendLine("foreach ($candidatePidValue in $targetPidValues) {");
                ps1.AppendLine("    $candidateDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine($"        $_.InstanceId -like \"USB\\VID_{config.VidValue}&PID_$candidatePidValue*\"");
                ps1.AppendLine("    })");
                ps1.AppendLine("    if ($null -ne $candidateDevices) { $selectedPidValue = $candidatePidValue; break }");
                ps1.AppendLine("}");
                ps1.AppendLine("if ($null -eq $selectedPidValue) {");
                ps1.AppendLine($"    Write-Output \"No present {config.DisplayName} matched supported PIDs: $($targetPidValues -join ', ').\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine($"    Write-Output 'results=' > {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"    Write-Output 'packages=' >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"    Write-Output 'pid=' >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine("    exit 2");
                ps1.AppendLine("}");
                ps1.AppendLine("$selectedPid = \"0x$selectedPidValue\"");
                ps1.AppendLine($"Write-Output \"Selected VID_{config.VidValue}&PID_$selectedPidValue.\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("Write-Host '=======================================' -ForegroundColor Cyan");
                ps1.AppendLine($"Write-Host '  Installing WinUSB: {config.DisplayName}' -ForegroundColor Cyan");
                ps1.AppendLine("Write-Host '=======================================' -ForegroundColor Cyan");
                ps1.AppendLine("Write-Host 'Preparing the environment (closing iCUE)...' -ForegroundColor Yellow");
                ps1.AppendLine($"Write-Output 'Killing Corsair/iCUE processes and services...' >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine(BuildCorsairShutdownScript());
                ps1.AppendLine("Start-Sleep -Seconds 3");
                ps1.AppendLine("Write-Host '[OK] Processes and services stopped.' -ForegroundColor Green");
                ps1.AppendLine("Write-Host 'Installing drivers... Accept any Windows security prompts that appear.' -ForegroundColor Yellow");

                foreach (var iface in config.Interfaces)
                {
                    ps1.AppendLine($"New-Item -ItemType Directory -Force -Path {PowerShellLiteral($"{tempDirPrefix}_mi{iface.Id}")} | Out-Null");
                }

                ps1.AppendLine("function Install-WdiDriver {");
                ps1.AppendLine("    param([string]$tempDir, [int]$interfaceId, [string]$miString)");
                ps1.AppendLine("    Write-Host \"-> Installing interface $($interfaceId)...\" -ForegroundColor Cyan");
                ps1.AppendLine($"    Write-Output \"Running $($miString)...\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("    try {");
                ps1.AppendLine("        $wdiOut = Join-Path -Path $tempDir -ChildPath 'wdi_out.log'");
                ps1.AppendLine("        $wdiErr = Join-Path -Path $tempDir -ChildPath 'wdi_err.log'");
                ps1.AppendLine($"        $argsArray = @('-n', {PowerShellLiteral(config.DeviceName)}, '-v', {PowerShellLiteral(config.Vid)}, '-p', $selectedPid, '-i', $interfaceId, '-t', '0', '-s', '-d', $tempDir)");
                ps1.AppendLine($"        $proc = Start-Process -FilePath {PowerShellLiteral(wdiPath)} -ArgumentList $argsArray -PassThru -NoNewWindow -RedirectStandardOutput $wdiOut -RedirectStandardError $wdiErr");
                ps1.AppendLine("        $maxWait = 60");
                ps1.AppendLine("        $waited = 0");
                ps1.AppendLine("        $miHex = '{0:X2}' -f $interfaceId");
                ps1.AppendLine("        while (-not $proc.HasExited -and $waited -lt $maxWait) {");
                ps1.AppendLine("            Start-Sleep -Seconds 3");
                ps1.AppendLine("            $waited += 3");
                ps1.AppendLine("            $devs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine($"                $_.InstanceId -like \"USB\\VID_{config.VidValue}&PID_$selectedPidValue&MI_$miHex*\"");
                ps1.AppendLine("            })");
                ps1.AppendLine("            if ($null -ne $devs) {");
                ps1.AppendLine("                if ($devs.Service -match 'WINUSB' -or $devs.Service -match 'WinUSB') {");
                ps1.AppendLine($"                    Write-Output \"WINUSB detected on $($miString). Stopping the unresponsive wdi-simple process.\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("                    break");
                ps1.AppendLine("                }");
                ps1.AppendLine("            }");
                ps1.AppendLine("        }");
                ps1.AppendLine("        if (-not $proc.HasExited) {");
                ps1.AppendLine($"            Write-Output \"Timeout reached for $($miString). Force killing.\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("        }");
                ps1.AppendLine("        Start-Sleep -Seconds 2");
                ps1.AppendLine("        $finalDevs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {");
                ps1.AppendLine($"            $_.InstanceId -like \"USB\\VID_{config.VidValue}&PID_$selectedPidValue&MI_$miHex*\"");
                ps1.AppendLine("        })");
                ps1.AppendLine("        if ($null -ne $finalDevs -and ($finalDevs.Service -match 'WINUSB' -or $finalDevs.Service -match 'WinUSB')) {");
                ps1.AppendLine("            $ret = 0");
                ps1.AppendLine("        } else {");
                ps1.AppendLine("            $ret = -1");
                ps1.AppendLine($"            Write-Output \"Failed: WINUSB not applied to $($miString). ExitCode: $($proc.ExitCode)\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine($"            if (Test-Path $wdiErr) {{ Write-Output \"wdi-simple stderr: $(Get-Content $wdiErr -Raw)\" >> {PowerShellLiteral(wdiLog)} }}");
                ps1.AppendLine($"            if (Test-Path $wdiOut) {{ Write-Output \"wdi-simple stdout: $(Get-Content $wdiOut -Raw)\" >> {PowerShellLiteral(wdiLog)} }}");
                ps1.AppendLine("        }");
                ps1.AppendLine("    } catch {");
                ps1.AppendLine($"        Write-Output \"Error $($miString): $($_.Exception.Message)\" >> {PowerShellLiteral(wdiLog)}");
                ps1.AppendLine("        $ret = -999");
                ps1.AppendLine("    }");
                ps1.AppendLine("    if ($ret -eq 0) {");
                ps1.AppendLine("        Write-Host \"   [OK] Interface $interfaceId installed successfully.\" -ForegroundColor Green");
                ps1.AppendLine("        return 0");
                ps1.AppendLine("    } else {");
                ps1.AppendLine("        Write-Host \"   [ERROR] Interface $interfaceId failed (see $wdiErr). ExitCode: $($proc.ExitCode)\" -ForegroundColor Red");
                ps1.AppendLine("        return -1");
                ps1.AppendLine("    }");
                ps1.AppendLine("}");

                ps1.AppendLine("$resultCodes = @()");
                foreach (var iface in config.Interfaces)
                {
                    ps1.AppendLine($"$resultCodes += Install-WdiDriver -tempDir {PowerShellLiteral($"{tempDirPrefix}_mi{iface.Id}")} -interfaceId {iface.Id} -miString {PowerShellLiteral(iface.Name)}");
                    ps1.AppendLine("Start-Sleep -Seconds 4");
                }

                ps1.AppendLine("$publishedInfsAfter = @(Get-PublishedInfNames)");
                ps1.AppendLine("$createdInfs = @($publishedInfsAfter | Where-Object { $publishedInfsBefore -notcontains $_ })");
                ps1.AppendLine("$ownedInfs = @()");
                ps1.AppendLine("foreach ($infName in $createdInfs) {");
                ps1.AppendLine("    $infPath = Join-Path (Join-Path $env:windir 'INF') $infName");
                ps1.AppendLine("    $content = Get-Content -LiteralPath $infPath -Raw -ErrorAction SilentlyContinue");
                ps1.AppendLine($"    if ($content -match 'ZeroCue' -and $content -match \"VID_{config.VidValue}&PID_$selectedPidValue\") {{ $ownedInfs += $infName }}");
                ps1.AppendLine("}");
                ps1.AppendLine($"Write-Output \"results=$($resultCodes -join ',')\" > {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"packages=$($ownedInfs -join ',')\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine($"Write-Output \"pid=$selectedPidValue\" >> {PowerShellLiteral(resultPath)}");
                ps1.AppendLine(BuildCorsairRestartScript());
                ps1.AppendLine("Write-Host '=======================================' -ForegroundColor Cyan");
                ps1.AppendLine("if ($resultCodes -notcontains -1) {");
                ps1.AppendLine("    Write-Host '[SUCCESS] Installation completed successfully.' -ForegroundColor Green");
                ps1.AppendLine("    Write-Host 'This window will close automatically...' -ForegroundColor DarkGray");
                ps1.AppendLine("    Start-Sleep -Seconds 3");
                ps1.AppendLine("} else {");
                ps1.AppendLine("    Write-Host '[WARNING] Installation completed with errors.' -ForegroundColor Red");
                ps1.AppendLine("    Write-Host 'Press ENTER to exit and review the logs...' -ForegroundColor Yellow");
                ps1.AppendLine("    Read-Host");
                ps1.AppendLine("}");
                ps1.AppendLine("Stop-Process -Name 'wdi-simple' -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("Stop-Process -Name 'installer_x64' -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("if ($resultCodes -notcontains -1) {");
                ps1.AppendLine($"    Get-ChildItem -Path {PowerShellLiteral(workDir)} -Filter 'zerocue_wdi_{config.LogName}_*' -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue");
                ps1.AppendLine("}");
                File.WriteAllText(ps1Path, ps1.ToString());
                Log($"Created {config.LogName} install script at: {ps1Path}");

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

                    SaveOwnedDriverPackages(config, newlyOwnedPackages);
                    Log($"{config.LogName} install results: {string.Join(",", resultCodes)}; owned packages: {string.Join(",", newlyOwnedPackages)}");
                    return resultCodes.Length == config.Interfaces.Length && resultCodes.All(code => code == "0");
                }

                Log($"{config.LogName} install result file not found");
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
                LogFileContents(wdiLog, $"{config.LogName} WDI output");
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        private async Task<bool> RestoreDefaultDriversAsync(DriverTarget target)
        {
            var config = DriverTargetConfig.For(target);
            var ownedPackages = LoadOwnedDriverPackages(config);
            bool allowLegacyDiscovery = ownedPackages.Count == 0;
            Log(allowLegacyDiscovery
                ? $"No ownership manifest exists for {config.LogName}; elevated restore will attempt a strictly scoped legacy migration."
                : $"Loaded {ownedPackages.Count} owned driver package(s) for {config.LogName} restore.");

            string workDir = Path.Combine(Path.GetTempPath(), "ZeroCue", "driver", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            string ps1Path = Path.Combine(workDir, $"zerocue_restore_{config.LogName}_drivers.ps1");
            string resultPath = Path.Combine(workDir, $"zerocue_restore_{config.LogName}_result.txt");

            try
            {
                if (File.Exists(resultPath))
                    File.Delete(resultPath);

                string ps1Content = BuildRestoreDriverScript(config, ownedPackages, resultPath, allowLegacyDiscovery);

                File.WriteAllText(ps1Path, ps1Content);
                Log($"Created {config.LogName} restore script at: {ps1Path}");

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
                    Log($"{config.LogName} restore script ExitCode: {process.ExitCode}");
                }

                if (File.Exists(resultPath))
                {
                    var result = ParseKeyValueResult(resultPath);
                    bool success = result.TryGetValue("success", out var successValue) &&
                        bool.TryParse(successValue, out var parsedSuccess) &&
                        parsedSuccess;
                    Log($"{config.LogName} restore results: {string.Join("; ", result.Select(pair => $"{pair.Key}={pair.Value}"))}");
                    if (success)
                    {
                        DeleteOwnedDriverPackageManifest(config);
                    }

                    return success;
                }

                Log($"{config.LogName} restore result file not found");
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
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        private static string BuildRestoreDriverScript(
            DriverTargetConfig config,
            IReadOnlyList<string> ownedPackages,
            string resultPath,
            bool allowLegacyDiscovery)
        {
            var ownedInfArray = string.Join(",", ownedPackages.Select(PowerShellLiteral));
            var targetPidArray = string.Join(",", config.RestorePidValues.Select(pid => PowerShellLiteral($"PID_{pid}")));
            var legacyHardwareIdArray = string.Join(",", config.PidValues.SelectMany(pid =>
                config.Interfaces.Select(iface =>
                    PowerShellLiteral($"VID_{config.VidValue}&PID_{pid}&MI_{iface.Id:X2}"))));
            return
                BuildCorsairShutdownScript() +
                "Start-Sleep -Seconds 3\r\n" +
                $"$ownedInfs = @({ownedInfArray})\r\n" +
                $"$allowLegacyDiscovery = ${allowLegacyDiscovery.ToString().ToLowerInvariant()}\r\n" +
                $"$legacyHardwareIds = @({legacyHardwareIdArray})\r\n" +
                $"$targetVid = 'VID_{config.VidValue}'\r\n" +
                $"$targetPids = @({targetPidArray})\r\n" +
                "$verifiedInfs = @()\r\n" +
                "$removedCount = 0\r\n" +
                "$removedDeviceCount = 0\r\n" +
                "$missingCount = 0\r\n" +
                "$failureCount = 0\r\n" +
                "$legacyPackageCount = 0\r\n" +
                "$targetDevices = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {\r\n" +
                "    $instanceId = $_.InstanceId\r\n" +
                "    $hasTargetId = $false\r\n" +
                "    foreach ($targetPid in $targetPids) { if ($instanceId -match [regex]::Escape(\"$targetVid&$targetPid\")) { $hasTargetId = $true; break } }\r\n" +
                "    $hasTargetId\r\n" +
                "})\r\n" +
                "if ($allowLegacyDiscovery) {\r\n" +
                "    $legacyCandidates = @(Get-ChildItem -Path (Join-Path $env:windir 'INF\\oem*.inf') -File -ErrorAction SilentlyContinue)\r\n" +
                "    foreach ($infFile in $legacyCandidates) {\r\n" +
                "        $content = Get-Content -LiteralPath $infFile.FullName -Raw -ErrorAction SilentlyContinue\r\n" +
                "        $hasExactHardwareId = $false\r\n" +
                "        foreach ($hardwareId in $legacyHardwareIds) { if ($content -match [regex]::Escape($hardwareId)) { $hasExactHardwareId = $true; break } }\r\n" +
                "        $hasLegacyIdentity = $content -match '(?im)^\\s*DeviceName\\s*=\\s*\"SCUF\"\\s*$' -and\r\n" +
                "            $content -match '(?im)^\\s*SourceName\\s*=\\s*\"SCUF Install Disk\"\\s*$' -and\r\n" +
                "            $content -match '(?im)^\\s*Provider\\s*=\\s*\"libwdi\"\\s*$' -and\r\n" +
                "            $content -match '(?im)^\\s*Class\\s*=\\s*\"USBDevice\"\\s*$' -and\r\n" +
                "            $content -match '(?im)^\\s*CatalogFile\\s*=\\s*usb_device\\.cat\\s*$' -and\r\n" +
                "            $content -match '(?im)^\\s*DriverVer\\s*=\\s*04/18/2019,\\s*6\\.1\\.7600\\.16385\\s*$'\r\n" +
                "        $isLegacyZeroCueWinUsb = $content -match 'WinUSBDeviceClassReg' -and $content -match 'AddService\\s*=\\s*WinUSB'\r\n" +
                "        if ($hasExactHardwareId -and $hasLegacyIdentity -and $isLegacyZeroCueWinUsb) {\r\n" +
                "            $verifiedInfs += $infFile.Name\r\n" +
                "        }\r\n" +
                "    }\r\n" +
                "    $verifiedInfs = @($verifiedInfs | Sort-Object -Unique)\r\n" +
                "    $legacyPackageCount = $verifiedInfs.Count\r\n" +
                "}\r\n" +
                "foreach ($infName in $ownedInfs) {\r\n" +
                "    $infPath = Join-Path (Join-Path $env:windir 'INF') $infName\r\n" +
                "    if (-not (Test-Path -LiteralPath $infPath -PathType Leaf)) { $missingCount++; continue }\r\n" +
                "    $content = Get-Content -LiteralPath $infPath -Raw -ErrorAction SilentlyContinue\r\n" +
                "    $hasTargetId = $false\r\n" +
                "    foreach ($targetPid in $targetPids) { if ($content -match [regex]::Escape(\"$targetVid&$targetPid\")) { $hasTargetId = $true; break } }\r\n" +
                "    if ($content -notmatch 'ZeroCue' -or -not $hasTargetId) {\r\n" +
                "        Write-Warning \"Refusing unverified driver package $infName\"\r\n" +
                "        $failureCount++\r\n" +
                "        continue\r\n" +
                "    }\r\n" +
                "    $verifiedInfs += $infName\r\n" +
                "}\r\n" +
                "$verifiedInfs = @($verifiedInfs | Sort-Object -Unique)\r\n" +
                "if ($verifiedInfs.Count -eq 0) {\r\n" +
                "    Write-Warning 'No verified ZeroCue driver package was found for this device.'\r\n" +
                "    $failureCount++\r\n" +
                "}\r\n" +
                "$ownedDevices = @($targetDevices | ForEach-Object {\r\n" +
                "    $device = $_\r\n" +
                "    $driverInf = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data\r\n" +
                "    if ($verifiedInfs -contains $driverInf) { $device }\r\n" +
                "})\r\n" +
                "$ownedDevices | Sort-Object @{Expression={$_.InstanceId.Length}; Descending=$true} | ForEach-Object {\r\n" +
                "    pnputil /remove-device \"$($_.InstanceId)\"\r\n" +
                "    if ($LASTEXITCODE -eq 0) { $removedDeviceCount++ } else { $failureCount++ }\r\n" +
                "}\r\n" +
                "foreach ($infName in $verifiedInfs) {\r\n" +
                "    pnputil /delete-driver $infName /uninstall /force\r\n" +
                "    if ($LASTEXITCODE -eq 0) { $removedCount++ } else { $failureCount++ }\r\n" +
                "}\r\n" +
                "pnputil /scan-devices | Out-Null\r\n" +
                "Stop-Process -Name 'wdi-simple' -Force -ErrorAction SilentlyContinue\r\n" +
                "Stop-Process -Name 'installer_x64' -Force -ErrorAction SilentlyContinue\r\n" +
                BuildCorsairRestartScript() +
                $"Write-Output \"success=$($failureCount -eq 0)\" > {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"removed=$removedCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"devices=$removedDeviceCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"legacy=$legacyPackageCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"missing=$missingCount\" >> {PowerShellLiteral(resultPath)}\r\n" +
                $"Write-Output \"failures=$failureCount\" >> {PowerShellLiteral(resultPath)}\r\n";
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
                var expectedPids = config.RestorePidValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var manifestPids = manifest?.PidValues?.ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool identityMatches = manifest != null &&
                    string.Equals(manifest.Target, config.LogName, StringComparison.Ordinal) &&
                    string.Equals(manifest.VidValue, config.VidValue, StringComparison.OrdinalIgnoreCase) &&
                    manifestPids != null &&
                    manifestPids.Count > 0 &&
                    manifestPids.IsSubsetOf(expectedPids);

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
            var allOwnedPackages = LoadOwnedDriverPackages(config)
                .Concat(newlyOwnedPackages.Where(IsPublishedInfName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (allOwnedPackages.Length == 0)
            {
                return;
            }

            string manifestPath = GetDriverPackageManifestPath(config);
            string manifestDirectory = Path.GetDirectoryName(manifestPath)!;
            Directory.CreateDirectory(manifestDirectory);

            var manifest = new DriverPackageManifest
            {
                Target = config.LogName,
                VidValue = config.VidValue,
                PidValues = config.RestorePidValues,
                PublishedInfNames = allOwnedPackages
            };
            var options = new JsonSerializerOptions { WriteIndented = true };
            string temporaryPath = manifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, options));
            File.Move(temporaryPath, manifestPath, overwrite: true);
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
                string script =
                    "$devices = Get-PnpDevice | Where-Object {\r\n" +
                    "    ($_.InstanceId -match 'VID_1B1C&PID_3A08') -or\r\n" +
                    "    ($_.InstanceId -match 'VID_1B1C&PID_3A09')\r\n" +
                    "} | Where-Object {\r\n" +
                    "    $_.InstanceId -notmatch 'PID_3A05'\r\n" +
                    "} | Select-Object InstanceId, Status, Class\r\n" +
                    "if ($devices) {\r\n" +
                    "    $devices | Format-Table -AutoSize | Out-String\r\n" +
                    "} else {\r\n" +
                    "    Write-Output 'No wireless receiver PnP instances were found.'\r\n" +
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
                        return output + "\nError:\n" + error;
                    }
                    return output;
                }
                return "No se pudo ejecutar PowerShell.";
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }

        private sealed class DriverPackageManifest
        {
            public string Target { get; init; } = string.Empty;
            public string VidValue { get; init; } = string.Empty;
            public string[] PidValues { get; init; } = Array.Empty<string>();
            public string[] PublishedInfNames { get; init; } = Array.Empty<string>();
        }

        private enum DriverTarget
        {
            Gamepad,
            Receiver
        }

        private sealed record DriverInterface(int Id, string Name);

        private sealed record DriverTargetConfig(
            string LogName,
            string DisplayName,
            string DeviceName,
            string Vid,
            string[] Pids,
            DriverInterface[] Interfaces)
        {
            public string VidValue => Vid.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            public string[] PidValues => Pids
                .Select(pid => pid.Replace("0x", "", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            public string[] RestorePidValues => PidValues;

            public static DriverTargetConfig For(DriverTarget target)
            {
                return target switch
                {
                    DriverTarget.Receiver => new DriverTargetConfig(
                        "receiver",
                        "SCUF RF receiver",
                        "ZeroCue SCUF Envision Pro Receiver",
                        "0x1B1C",
                        new[] { "0x3A08", "0x3A09" },
                        new[]
                        {
                            new DriverInterface(4, "MI_04"),
                            new DriverInterface(3, "MI_03")
                        }),
                    _ => new DriverTargetConfig(
                        "gamepad",
                        "SCUF Envision wired controller",
                        "ZeroCue SCUF Envision Wired",
                        "0x1B1C",
                        new[] { "0x3A05", "0x3A04" },
                        new[]
                        {
                            new DriverInterface(0, "MI_00"),
                            new DriverInterface(4, "MI_04"),
                            new DriverInterface(3, "MI_03")
                        })
                };
            }
        }
    }
}
