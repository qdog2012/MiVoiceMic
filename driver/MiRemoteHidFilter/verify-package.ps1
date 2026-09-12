param([string]$PackageDirectory = (Join-Path $PSScriptRoot 'package'))
$ErrorActionPreference = 'Stop'

# Verify catalog membership without importing certificates or changing Windows.
# Authenticode hashes for PE files differ from ordinary Get-FileHash hashes.
if (-not ('MiRemote.PackageCatalog' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace MiRemote {
    public static class PackageCatalog {
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CryptCATAdminAcquireContext2(out IntPtr context, IntPtr subsystem,
            string algorithm, IntPtr policy, uint flags);
        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr context, IntPtr file,
            ref uint size, [Out] byte[] hash, uint flags);
        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATAdminReleaseContext(IntPtr context, uint flags);
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CryptCATOpen(string file, uint flags, IntPtr provider, uint version, uint encoding);
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CryptCATGetMemberInfo(IntPtr catalog, string tag);
        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATClose(IntPtr catalog);

        public static bool Contains(string catalogPath, string filePath) {
            IntPtr context;
            if (!CryptCATAdminAcquireContext2(out context, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                byte[] hash;
                using (var file = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    uint size = 0;
                    if (!CryptCATAdminCalcHashFromFileHandle2(context, file.SafeFileHandle.DangerousGetHandle(), ref size, null, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    hash = new byte[size];
                    if (!CryptCATAdminCalcHashFromFileHandle2(context, file.SafeFileHandle.DangerousGetHandle(), ref size, hash, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                IntPtr catalog = CryptCATOpen(catalogPath, 0, IntPtr.Zero, 0, 0);
                if (catalog == IntPtr.Zero || catalog == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try { return CryptCATGetMemberInfo(catalog, BitConverter.ToString(hash).Replace("-", "")) != IntPtr.Zero; }
                finally { CryptCATClose(catalog); }
            } finally { CryptCATAdminReleaseContext(context, 0); }
        }
    }
}
'@
}

$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($name in @('MiRemoteHidFilter.inf', 'MiRemoteHidFilter.sys', 'miremotehidfilter.cat', 'MiRemoteHidFilter.cer')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $name) -PathType Leaf)) {
        throw "Driver package is incomplete: missing $name. Use the complete package directory."
    }
}

Add-Type -AssemblyName System.Security
$catalogPath = Join-Path $packagePath 'miremotehidfilter.cat'
$cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
$cms.Decode([IO.File]::ReadAllBytes($catalogPath))
$cms.CheckSignature($true) # Cryptographic integrity only; trust is checked separately at installation.
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $packagePath 'MiRemoteHidFilter.cer')
if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'The package certificate does not match the catalog signer.'
}
if ((Get-Date) -lt $certificate.NotBefore -or (Get-Date) -gt $certificate.NotAfter) {
    throw 'The driver certificate is outside its validity period.'
}
foreach ($name in @('MiRemoteHidFilter.inf', 'MiRemoteHidFilter.sys')) {
    if (-not [MiRemote.PackageCatalog]::Contains($catalogPath, (Join-Path $packagePath $name))) {
        throw "Catalog hash mismatch: $name (0xE000024B). Restore the original signed package; do not edit its INF or normalize its line endings."
    }
    Write-Host "Catalog matches: $name"
}
Write-Host 'Package integrity verified. This does not enable test mode or install the driver.' -ForegroundColor Green
