$ErrorActionPreference = 'Stop'
$driverDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'driver\MiRemoteHidFilter'
$verifier = Join-Path $driverDir 'verify-package.ps1'
$original = Join-Path $driverDir 'package'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('MiVoiceMic-package-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$files = @('MiRemoteHidFilter.inf', 'MiRemoteHidFilter.sys', 'miremotehidfilter.cat', 'MiRemoteHidFilter.cer')
function Restore-Package {
    foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $original $name) -Destination (Join-Path $scratch $name) -Force }
}
function Assert-Rejected([string]$label) {
    $rejected = $false
    try { & $verifier -PackageDirectory $scratch } catch { $rejected = $true }
    if (-not $rejected) { throw "FAIL: $label was accepted" }
    Write-Host "PASS: rejected $label"
}
try {
    Restore-Package
    & $verifier -PackageDirectory $scratch
    Write-Host 'PASS: original signed package verifies without importing its certificate'

    $inf = Join-Path $scratch 'MiRemoteHidFilter.inf'
    [IO.File]::WriteAllText($inf, ([IO.File]::ReadAllText($inf).Replace("`r`n", "`n")), (New-Object Text.UTF8Encoding($false)))
    Assert-Rejected 'INF with changed line endings'

    Restore-Package
    $sys = Join-Path $scratch 'MiRemoteHidFilter.sys'
    $bytes = [IO.File]::ReadAllBytes($sys)
    $bytes[4096] = $bytes[4096] -bxor 1
    [IO.File]::WriteAllBytes($sys, $bytes)
    Assert-Rejected 'modified driver binary'

    Restore-Package
    Remove-Item -LiteralPath (Join-Path $scratch 'MiRemoteHidFilter.cer')
    Assert-Rejected 'incomplete package'
    Write-Host 'PASS: all 4 package regression checks passed'
} finally {
    # Only remove the four files this test created, then the empty directory.
    foreach ($name in $files) {
        $path = Join-Path $scratch $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $scratch
}
