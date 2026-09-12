param([switch]$Online)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # The application targets .NET Framework, so reflection must run in Windows PowerShell.
    $testArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Online) { $testArgs += '-Online' }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Update integration test failed in Windows PowerShell' }
    return
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$appExe = Join-Path $repoRoot 'MiVoiceMic.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$appAssembly = [Reflection.Assembly]::LoadFile($appExe)
$updater = $appAssembly.GetType('AppUpdater', $true)
$current = $appAssembly.GetName().Version
$nextVersion = '{0}.{1}.{2}' -f $current.Major, $current.Minor, ($current.Build + 1)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MiVoiceMic update test ' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $testRoot ('.MiVoiceMic-update-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$parent = $null
try {
    # Two harmless fixture programs exercise a real running-image replacement. No BLE or hooks.
    $parentCode = @'
using System;
using System.IO;
using System.Threading;
class Probe {
    static void Main() {
        string stop = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "parent-stop");
        for (int i = 0; i < 300 && !File.Exists(stop); i++) Thread.Sleep(100);
    }
}
'@
    $childCode = @'
using System;
using System.IO;
using System.Threading;
using System.Reflection;
[assembly: AssemblyProduct("MiVoiceMic")]
[assembly: AssemblyVersion("VERSION.0")]
[assembly: AssemblyFileVersion("VERSION.0")]
class Probe {
    static void Main() {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "new-started"), "ok");
        Thread.Sleep(7000);
    }
}
'@
    $parentSource = Join-Path $testRoot 'parent.cs'
    $childSource = Join-Path $testRoot 'child.cs'
    $target = Join-Path $testRoot 'MiVoiceMic.exe'
    $payload = Join-Path $stage 'payload.exe'
    [IO.File]::WriteAllText($parentSource, $parentCode, $utf8)
    [IO.File]::WriteAllText($childSource, $childCode.Replace('VERSION', $nextVersion), $utf8)
    & $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$target" $parentSource
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile parent fixture' }
    & $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$payload" $childSource
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile child fixture' }
    $oldHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $newHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    $config = Join-Path $testRoot 'config.json'
    [IO.File]::WriteAllText($config, '{"test":"preserve exact bytes"}', $utf8)
    $configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $appExe -Destination (Join-Path $stage 'updater.exe')
    $parent = Start-Process -FilePath $target -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $plan = @{
        Target = $target; Sha256 = $newHash; OriginalSha256 = $oldHash; Version = $nextVersion
        Size = (Get-Item -LiteralPath $payload).Length; ParentId = $parent.Id
        ParentStarted = $parent.StartTime.ToUniversalTime().Ticks
    }
    [IO.File]::WriteAllText((Join-Path $stage 'update.json'), ($plan | ConvertTo-Json), $utf8)
    $updater.GetMethod('StartHelper').Invoke($null, @([string]$stage, [Threading.CancellationToken]::None)) | Out-Null
    if ($parent.HasExited) { throw 'The helper did not wait for the running parent' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $oldHash) { throw 'Replaced a running parent too soon' }
    [IO.File]::WriteAllText((Join-Path $testRoot 'parent-stop'), 'stop', $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $stage 'result.txt'))) {
        if (Test-Path -LiteralPath (Join-Path $stage 'error.txt')) { throw (Get-Content -LiteralPath (Join-Path $stage 'error.txt') -Raw) }
        if ($watch.Elapsed.TotalSeconds -gt 20) { throw 'Update helper did not complete' }
        Start-Sleep -Milliseconds 200
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $newHash -or
        (Get-FileHash -LiteralPath (Join-Path $stage 'previous.exe') -Algorithm SHA256).Hash -ne $oldHash -or
        -not (Test-Path -LiteralPath (Join-Path $testRoot 'new-started')) -or
        (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -ne $configHash) { throw 'Update postconditions failed' }
    Write-Host 'PASS: real helper waits for parent, replaces EXE, backs up original, restarts and preserves config'

    if ($Online) {
        $release = $updater.GetMethod('Check').Invoke($null, @([Threading.CancellationToken]::None))
        $download = Join-Path $testRoot 'github-latest.exe'
        $output = [IO.File]::Create($download)
        try {
            $fetch = $updater.GetMethod('Fetch', [Reflection.BindingFlags]'NonPublic,Static')
            $fetch.Invoke($null, @([string]$release.DownloadUrl, $output, [long]$release.Size, $null, [Threading.CancellationToken]::None)) | Out-Null
        } finally { $output.Dispose() }
        $updater.GetMethod('VerifyFile').Invoke($null, @([string]$download, [long]$release.Size, [string]$release.Sha256)) | Out-Null
        Write-Host ('PASS: GitHub {0} executable downloaded and SHA256 verified; no local downgrade performed' -f $release.Tag)
    }
} finally {
    # Only test processes from this unique directory are eligible for cleanup.
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })) {
        if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
    }
    if ($parent) { $parent.Dispose() }
    $resolvedTest = (Resolve-Path -LiteralPath $testRoot).Path
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTest.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTest) -notmatch '^MiVoiceMic update test [a-f0-9]{32}$') { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolvedTest -Recurse -Force
}
