# Query the running kernel, not just the boot configuration for the next restart.
if (-not ('MiRemote.KernelCodeIntegrity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MiRemote {
    public static class KernelCodeIntegrity {
        [StructLayout(LayoutKind.Sequential)]
        struct Information { public uint Length; public uint Options; }
        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int informationClass, ref Information info, uint length, out uint returned);
        public static bool TestSigningEnabled() {
            var info = new Information { Length = 8 };
            uint returned;
            int status = NtQuerySystemInformation(103, ref info, info.Length, out returned);
            if (status != 0) throw new InvalidOperationException("Cannot read active test mode: NTSTATUS 0x" + status.ToString("X8"));
            return (info.Options & 2) != 0;
        }
    }
}
'@
}
[MiRemote.KernelCodeIntegrity]::TestSigningEnabled()
