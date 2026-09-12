using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

static class UpdateTests {
    static string Fixture(string tag, bool draft, bool prerelease, string url, string digest, bool includeExe) {
        var assets = new List<object>();
        if (includeExe) assets.Add(new { name = "MiVoiceMic.exe", state = "uploaded", size = 123,
            browser_download_url = url, digest = digest });
        return new JavaScriptSerializer().Serialize(new { tag_name = tag, draft = draft, prerelease = prerelease, assets = assets });
    }

    static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }

    public static void Run(Action<bool, string> check) {
        check(AppUpdater.ParseVersion("v1.10.0") > AppUpdater.ParseVersion("1.9.9"), "update: numeric version comparison");
        foreach (string value in new[] { "v1.2.3-beta", "1.2.3.4", "1.2", "01.2.3", "v1.2.3/../other", "999999999999.0.0" })
            check(Rejects(delegate { AppUpdater.ParseVersion(value); }), "update: rejects version " + value);
        string tag = "v99.0.0";
        string url = "https://github.com/qdog2012/MiVoiceMic/releases/download/" + tag + "/MiVoiceMic.exe";
        string hash = new string('a', 64);
        var release = AppUpdater.ParseRelease(Fixture(tag, false, false, url, "sha256:" + hash, true));
        check(release.IsNewer && release.Size == 123 && release.Sha256 == hash, "update: selects verified executable from stable release");
        check(!new AppRelease { Version = AppUpdater.ParseVersion(AppVersion.Number) }.IsNewer &&
            !new AppRelease { Version = AppUpdater.ParseVersion("1.0.0") }.IsNewer, "update: never offers same version or downgrade");
        check(Rejects(delegate { AppUpdater.ParseRelease(Fixture(tag, true, false, url, "sha256:" + hash, true)); }), "update: rejects draft");
        check(Rejects(delegate { AppUpdater.ParseRelease(Fixture(tag, false, true, url, "sha256:" + hash, true)); }), "update: rejects prerelease");
        check(Rejects(delegate { AppUpdater.ParseRelease(Fixture(tag, false, false, url, "sha256:" + hash, false)); }), "update: rejects missing executable");
        check(Rejects(delegate { AppUpdater.ParseRelease(Fixture(tag, false, false, url.Replace("qdog2012", "someone"), "sha256:" + hash, true)); }), "update: rejects foreign release asset");
        check(Rejects(delegate { AppUpdater.ParseRelease(Fixture(tag, false, false, url, null, true)); }), "update: rejects unverified release");
        check(Rejects(delegate { AppUpdater.ParseRelease(Fixture(tag, false, false, url, "sha256:bad", true)); }), "update: rejects invalid digest");
        check(AppUpdater.ParseChecksum(hash + "  archive.zip\r\n" + hash + "  MiVoiceMic.exe\r\n") == hash, "update: reads exact checksum filename");
        check(Rejects(delegate { AppUpdater.ParseChecksum(hash + "  MiVoiceMic.exe\n" + hash + "  MiVoiceMic.exe\n"); }), "update: rejects ambiguous checksum");
        check(Rejects(delegate { AppUpdater.ParseChecksum(hash + "  other/MiVoiceMic.exe\n"); }), "update: rejects checksum for other path");

        string root = Path.Combine(Path.GetTempPath(), "MiVoiceMic-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string target = Path.Combine(root, "遥控器 test.exe");
            string payload = Path.Combine(root, "payload.exe");
            string backup = Path.Combine(root, "previous.exe");
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, "preserve exactly");
            File.WriteAllText(target, "old");
            File.WriteAllText(payload, "new");
            string sha = AppUpdater.HashFile(payload);
            AppUpdater.VerifyFile(payload, 3, sha);
            check(true, "update: accepts matching size and SHA256");
            check(Rejects(delegate { AppUpdater.VerifyFile(payload, 4, sha); }), "update: rejects truncated download");
            File.WriteAllText(payload, "bad");
            check(Rejects(delegate { AppUpdater.VerifyFile(payload, 3, sha); }), "update: rejects same-size corruption");
            check(Rejects(delegate { AppUpdater.VerifyProgram(payload, new Version(AppVersion.Number)); }), "update: rejects non-executable payload");
            string self = System.Windows.Forms.Application.ExecutablePath;
            AppUpdater.VerifyProgram(self, new Version(AppVersion.Number));
            check(true, "update: validates x64 program product and assembly version");
            check(Rejects(delegate { AppUpdater.VerifyProgram(self, new Version("99.0.0")); }), "update: rejects mismatched executable version");
            File.WriteAllText(payload, "new");
            string restarted = null;
            AppUpdater.ReplaceAndRestart(payload, target, backup, delegate(string path) { restarted = path; });
            check(File.ReadAllText(target) == "new" && File.ReadAllText(backup) == "old" && restarted == target,
                "update: atomically replaces and restarts in path containing Chinese and spaces");
            check(File.ReadAllText(config) == "preserve exactly", "update: preserves adjacent configuration");
            File.Delete(backup);
            File.WriteAllText(payload, "broken-new");
            check(Rejects(delegate { AppUpdater.ReplaceAndRestart(payload, target, backup, delegate(string path) { throw new IOException("restart failed"); }); }) &&
                File.ReadAllText(target) == "new", "update: restores original when restart fails");
            File.WriteAllText(payload, "locked-new");
            using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                check(Rejects(delegate { AppUpdater.ReplaceAndRestart(payload, target, backup, delegate { }); }), "update: locked target times out safely");
            check(File.ReadAllText(target) == "new" && File.ReadAllText(payload) == "locked-new", "update: failed replacement preserves both files");
            string stage = Path.Combine(root, ".MiVoiceMic-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "payload.exe"), "partial");
            check(AppUpdater.IsStage(stage, root) && !AppUpdater.IsStage(root, root), "update: cleanup is confined to immediate generated staging directory");
            AppUpdater.CleanupStage(stage, root);
            check(!Directory.Exists(stage), "update: removes cancelled partial download");
            AppUpdater.CleanupStage(root, root);
            check(File.Exists(config), "update: cleanup refuses application directory");
            using (var cancelled = new CancellationTokenSource()) {
                cancelled.Cancel();
                check(Rejects(delegate { AppUpdater.Check(cancelled.Token); }), "update: cancellation stops request before network I/O");
            }
        } finally {
            // Unique test directory under temp, with only files created above, no recursive deletion.
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root, false);
        }
    }
}
