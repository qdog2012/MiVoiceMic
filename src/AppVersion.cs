using System.Reflection;

[assembly: AssemblyTitle("MiVoiceMic")]
[assembly: AssemblyProduct("MiVoiceMic")]
[assembly: AssemblyVersion(AppVersion.Number + ".0")]
[assembly: AssemblyFileVersion(AppVersion.Number + ".0")]

static class AppVersion {
    // Keep the release tag and packaged executable in sync (checked by package-release.ps1).
    public const string Number = "1.0.6";
}
