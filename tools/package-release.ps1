param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Use a stable version such as v1.0.1.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts\release\$Version" }
if (Test-Path -LiteralPath $OutputDirectory) {
    if (Get-ChildItem -LiteralPath $OutputDirectory -Force) { throw 'The release output directory must be empty.' }
} else {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) ('MiVoiceMic-release-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null

# Allowlist distribution files: never include local config, recordings or logs.
foreach ($name in @('MiVoiceMic.exe', 'README.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $stage
}
foreach ($name in @('setup', 'driver')) {
    $tracked = @(& git -C $repoRoot ls-files -- $name)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw "Could not list distribution files: $name" }
    foreach ($relative in $tracked) {
        $target = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot $relative) -Destination $target
    }
}
New-Item -ItemType Directory -Path (Join-Path $stage 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\NON-STANDARD-KEYS.md') -Destination (Join-Path $stage 'docs')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\screenshots') -Destination (Join-Path $stage 'docs') -Recurse
& (Join-Path $stage 'driver\MiRemoteHidFilter\verify-package.ps1')

$exe = Join-Path $output 'MiVoiceMic.exe'
$zip = Join-Path $output "MiVoiceMic-$Version-win-x64.zip"
Copy-Item -LiteralPath (Join-Path $stage 'MiVoiceMic.exe') -Destination $exe
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$checksums = foreach ($path in @($exe, $zip)) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($path)
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $checksums, (New-Object Text.UTF8Encoding($false)))
Write-Host "Release files: $output"
Get-ChildItem -LiteralPath $output -File | Select-Object Name, Length

# The staging directory is unique to this invocation. Validate before cleanup.
$resolvedStage = (Resolve-Path -LiteralPath $stage).Path
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $resolvedStage.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedStage) -notmatch '^MiVoiceMic-release-[a-f0-9]{32}$') {
    throw 'Unexpected staging path; refusing cleanup.'
}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
