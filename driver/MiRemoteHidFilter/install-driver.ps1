#Requires -RunAsAdministrator
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:LOCALAPPDATA 'MiVoiceMic\driver-logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logFile = Join-Path $logDir ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
Start-Transcript -Path $logFile -Force | Out-Null
try {
& (Join-Path $PSScriptRoot 'verify-package.ps1')

$package = Join-Path $PSScriptRoot 'package'
$inf = Join-Path $package 'MiRemoteHidFilter.inf'
$targetHardwareId = 'HID\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&012717_PID&32b8_REV&00a4'

if (-not (Test-Path $inf)) { throw "Driver package not found: $inf" }

if (-not (& (Join-Path $PSScriptRoot 'test-mode-state.ps1'))) {
    throw 'TESTSIGNING is not active in the running Windows kernel. Run prepare-test-mode.bat, restart Windows, then install. Enabling the boot option without restarting is not enough.'
}

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $package 'MiRemoteHidFilter.cer')
foreach ($store in @('Root', 'TrustedPublisher')) {
    if (-not (Test-Path -LiteralPath ("Cert:\LocalMachine\$store\" + $certificate.Thumbprint))) {
        throw "The driver certificate is not trusted in LocalMachine $store. Run prepare-test-mode.bat first."
    }
}

$device = Get-PnpDevice -Class Keyboard -PresentOnly | Where-Object {
    $ids = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction SilentlyContinue).Data
    $ids -contains $targetHardwareId
} | Select-Object -First 1
if (-not $device) { throw "Target Xiaomi keyboard collection is not present: $targetHardwareId" }

Write-Host "Target device: $($device.FriendlyName)"
Write-Host "Instance:      $($device.InstanceId)"
if ($CheckOnly) {
    Write-Host 'Preflight passed. No driver was installed.' -ForegroundColor Green
    return
}
Write-Host 'Staging and installing the device-specific extension INF...'
& pnputil.exe /add-driver $inf /install
$installExit = $LASTEXITCODE
if ($installExit -notin @(0, 3010)) { throw "pnputil failed with exit code $installExit. See this log and C:\Windows\INF\setupapi.dev.log." }

Write-Host ''
Write-Host 'Driver package installed successfully.' -ForegroundColor Green
try {
    $service = Get-CimInstance Win32_SystemDriver -Filter "Name='MiRemoteHidFilter'"
    $problem = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_ProblemCode).Data
    $nodeStatus = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_DevNodeStatus).Data
    if ($installExit -eq 0 -and $service.Started -and $problem -eq 0 -and ($nodeStatus -band 0x100) -eq 0) {
        Write-Host 'The driver is running and the target device has no errors. No additional restart is currently required.' -ForegroundColor Green
    } else {
        Write-Host 'Restart Windows to finish loading the driver on the remote keyboard.' -ForegroundColor Yellow
    }
} catch {
    Write-Host 'Could not confirm the live device state. Restart Windows, then check the remote keys.' -ForegroundColor Yellow
}
Write-Host 'Use MiVoiceMic to verify the remote keys. No automatic restart will be performed.'
} catch {
    Write-Host ('Installation failed: ' + $_.Exception.Message) -ForegroundColor Red
    throw
} finally {
    Stop-Transcript | Out-Null
    Write-Host "Log: $logFile"
}
