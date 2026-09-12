// GitHub stable releases, verified downloads and an independent replacement helper.
// No config, driver, recording or autostart files are changed by the updater.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

sealed class AppRelease {
    public string Tag, DownloadUrl, Sha256, ChecksumsUrl;
    public Version Version;
    public long Size;
    public bool IsNewer { get { return Version > new Version(AppVersion.Number); } }
}

sealed class UpdatePlan {
    public string Target, Sha256, OriginalSha256, Version;
    public long Size, ParentStarted;
    public int ParentId;
}

static class AppUpdater {
    public const string ReleasesUrl = "https://github.com/qdog2012/MiVoiceMic/releases/latest";
    const string ApiUrl = "https://api.github.com/repos/qdog2012/MiVoiceMic/releases/latest";
    const string DownloadRoot = "https://github.com/qdog2012/MiVoiceMic/releases/download/";
    const string StagePrefix = ".MiVoiceMic-update-";
    const long MaxExeSize = 64 * 1024 * 1024;
    public static bool Interactive; // Set only during normal GUI startup, never screenshot/selftest.

    public static Version ParseVersion(string text) {
        if (text == null || !Regex.IsMatch(text, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
            throw new InvalidDataException("发布版本号格式不正确。");
        Version result;
        if (!Version.TryParse(text.TrimStart('v'), out result))
            throw new InvalidDataException("发布版本号超出范围。");
        return result;
    }

    public static AppRelease Check(CancellationToken token) {
        return ParseRelease(ReadText(ApiUrl, 2 * 1024 * 1024, token));
    }

    static string Field(Dictionary<string, object> item, string key) {
        object value;
        return item.TryGetValue(key, out value) && value is string ? (string)value : "";
    }

    public static AppRelease ParseRelease(string json) {
        var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        if (data == null || !data.ContainsKey("draft") || !data.ContainsKey("prerelease") ||
            !object.Equals(data["draft"], false) || !object.Equals(data["prerelease"], false))
            throw new InvalidDataException("GitHub 未返回正式发布版本。");
        string tag = Field(data, "tag_name");
        var release = new AppRelease { Tag = tag, Version = ParseVersion(tag) };
        object raw;
        var assets = data.TryGetValue("assets", out raw) ? raw as System.Collections.IEnumerable : null;
        if (assets != null) foreach (object entry in assets) {
            var asset = entry as Dictionary<string, object>;
            if (asset == null || Field(asset, "state") != "uploaded") continue;
            string name = Field(asset, "name");
            if (name != "MiVoiceMic.exe" && name != "SHA256SUMS.txt") continue;
            string url = Field(asset, "browser_download_url");
            if (url != DownloadRoot + tag + "/" + name)
                throw new InvalidDataException("更新文件地址不属于本项目的 GitHub 发布。");
            if (name == "SHA256SUMS.txt") { release.ChecksumsUrl = url; continue; }
            if (release.DownloadUrl != null) throw new InvalidDataException("发布中存在重复程序文件。");
            release.DownloadUrl = url;
            if (!asset.TryGetValue("size", out raw) || !long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out release.Size) ||
                release.Size <= 0 || release.Size > MaxExeSize)
                throw new InvalidDataException("更新文件大小不正确。");
            string digest = Field(asset, "digest");
            if (digest.StartsWith("sha256:", StringComparison.Ordinal)) {
                release.Sha256 = digest.Substring(7);
                ValidateHash(release.Sha256);
            }
        }
        if (release.DownloadUrl == null) throw new InvalidDataException("最新版本尚未提供 MiVoiceMic.exe，请稍后重试。");
        if (release.Sha256 == null && release.ChecksumsUrl == null)
            throw new InvalidDataException("发布缺少完整性校验信息，无法自动更新。");
        return release;
    }

    static void ValidateHash(string hash) {
        if (hash == null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("SHA256 校验信息不正确。");
    }

    public static string ParseChecksum(string text) {
        var matches = Regex.Matches(text, @"(?m)^([a-fA-F0-9]{64}) [ *]MiVoiceMic\.exe\r?$");
        if (matches.Count != 1) throw new InvalidDataException("校验清单中没有唯一的 MiVoiceMic.exe。");
        return matches[0].Groups[1].Value;
    }

    static bool TrustedUrl(Uri uri) {
        return uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 &&
            (uri.Host == "api.github.com" || uri.Host == "github.com" ||
             uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
    }

    // Synchronous network I/O runs on a worker, with cancellation aborting the active request.
    // Limit both total duration and response length, including chunked responses.
    static void Fetch(string url, Stream output, long limit, Action<long> progress, CancellationToken token) {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            Uri uri = new Uri(url);
            for (int redirects = 0; redirects <= 5; redirects++) {
                timeout.Token.ThrowIfCancellationRequested();
                if (!TrustedUrl(uri)) throw new InvalidDataException("下载跳转地址不受支持。");
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.UserAgent = "MiVoiceMic/" + AppVersion.Number;
                request.Accept = uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream";
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AllowAutoRedirect = false;
                using (timeout.Token.Register(request.Abort)) {
                    try {
                        using (var response = (HttpWebResponse)request.GetResponse()) {
                            int code = (int)response.StatusCode;
                            if (code >= 300 && code < 400) {
                                string location = response.Headers[HttpResponseHeader.Location];
                                if (string.IsNullOrEmpty(location)) throw new InvalidDataException("下载跳转缺少地址。");
                                uri = new Uri(uri, location);
                                continue;
                            }
                            if (response.StatusCode != HttpStatusCode.OK) throw new IOException("GitHub 返回异常状态：" + code);
                            if (response.ContentLength > limit) throw new InvalidDataException("下载内容超出预期大小。");
                            using (Stream input = response.GetResponseStream()) {
                                var buffer = new byte[32768];
                                long total = 0;
                                int count;
                                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                                    timeout.Token.ThrowIfCancellationRequested();
                                    total += count;
                                    if (total > limit) throw new InvalidDataException("下载内容超出预期大小。");
                                    output.Write(buffer, 0, count);
                                    if (progress != null) progress(total);
                                }
                            }
                            return;
                        }
                    } catch (WebException ex) {
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("下载超时，请重试。", ex);
                        using (var response = ex.Response as HttpWebResponse) {
                            if (response != null && ((int)response.StatusCode == 403 || (int)response.StatusCode == 429))
                                throw new IOException("GitHub 暂时限制了请求，请稍后再试。", ex);
                        }
                        throw;
                    }
                }
            }
        }
        throw new IOException("下载跳转次数过多。");
    }

    static string ReadText(string url, int limit, CancellationToken token) {
        using (var stream = new MemoryStream()) {
            Fetch(url, stream, limit, null, token);
            return Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');
        }
    }

    public static string HashFile(string path) {
        using (var input = File.OpenRead(path)) using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    public static void VerifyFile(string path, long size, string sha256) {
        ValidateHash(sha256);
        if (new FileInfo(path).Length != size) throw new InvalidDataException("下载文件不完整，请重新下载。");
        if (!string.Equals(HashFile(path), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载文件校验失败，已停止更新，请重试。");
    }

    public static void VerifyProgram(string path, Version version) {
        var assembly = AssemblyName.GetAssemblyName(path);
        var file = FileVersionInfo.GetVersionInfo(path);
        if (file.ProductName != "MiVoiceMic" || assembly.ProcessorArchitecture != ProcessorArchitecture.Amd64 ||
            assembly.Version != new Version(version.Major, version.Minor, version.Build, 0))
            throw new InvalidDataException("下载程序的版本或架构与发布信息不一致。");
    }

    public static string Prepare(AppRelease release, Action<int> progress, CancellationToken token) {
        if (!release.IsNewer) throw new InvalidOperationException("当前版本无需更新。");
        string target = Application.ExecutablePath;
        string stage = Path.Combine(Path.GetDirectoryName(target), StagePrefix + Guid.NewGuid().ToString("N"));
        try {
            // Stage on the target volume so File.Replace remains atomic; also test write access now.
            Directory.CreateDirectory(stage);
            File.SetAttributes(stage, File.GetAttributes(stage) | FileAttributes.Hidden);
            string sha256 = release.Sha256 ?? ParseChecksum(ReadText(release.ChecksumsUrl, 65536, token));
            string payload = Path.Combine(stage, "payload.exe");
            using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Fetch(release.DownloadUrl, output, release.Size, delegate(long count) {
                    if (progress != null) progress((int)(count * 100 / release.Size));
                }, token);
            token.ThrowIfCancellationRequested();
            VerifyFile(payload, release.Size, sha256);
            VerifyProgram(payload, release.Version);
            using (var parent = Process.GetCurrentProcess()) {
                var plan = new UpdatePlan { Target = target, Sha256 = sha256, Version = release.Version.ToString(),
                    OriginalSha256 = HashFile(target),
                    Size = release.Size, ParentId = parent.Id, ParentStarted = parent.StartTime.ToUniversalTime().Ticks };
                File.WriteAllText(Path.Combine(stage, "update.json"), new JavaScriptSerializer().Serialize(plan), Encoding.UTF8);
            }
            File.Copy(target, Path.Combine(stage, "updater.exe"));
            return stage;
        } catch {
            CleanupStage(stage, Path.GetDirectoryName(target));
            throw;
        }
    }

    public static bool IsStage(string stage, string parent) {
        string full = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(Path.GetFileName(full), @"^\.MiVoiceMic-update-[a-f0-9]{32}$");
    }

    public static void CleanupStage(string stage, string parent) {
        // Never recurse through junctions or remove arbitrary paths from an update manifest.
        try {
            if (!IsStage(stage, parent) || !Directory.Exists(stage) ||
                (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string name in new[] { "payload.exe", "updater.exe", "update.json", "ready", "cancel" })
                File.Delete(Path.Combine(stage, name));
            Directory.Delete(stage, false);
        } catch { }
    }

    public static void StartHelper(string stage, CancellationToken token) {
        using (var helper = Process.Start(new ProcessStartInfo {
            FileName = Path.Combine(stage, "updater.exe"), Arguments = "--apply-update",
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage
        })) {
            try {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 15000) {
                    token.ThrowIfCancellationRequested();
                    if (helper.HasExited) {
                        string error = Path.Combine(stage, "error.txt");
                        throw new IOException(File.Exists(error) ? File.ReadAllText(error) : "无法启动更新助手。");
                    }
                    if (File.Exists(Path.Combine(stage, "ready"))) return;
                    Thread.Sleep(100);
                }
                throw new TimeoutException("更新助手启动超时，请重试。");
            } catch {
                File.WriteAllText(Path.Combine(stage, "cancel"), "cancel");
                throw;
            }
        }
    }

    // Exposed separately for real filesystem rollback/lock tests without stopping the user's app.
    public static void ReplaceAndRestart(string payload, string target, string backup, Action<string> restart) {
        var watch = Stopwatch.StartNew();
        while (true) {
            try { File.Replace(payload, target, backup); break; }
            catch (IOException) {
                if (watch.ElapsedMilliseconds >= 10000) throw;
                Thread.Sleep(200);
            }
        }
        try { restart(target); }
        catch {
            File.Replace(backup, target, null);
            throw;
        }
    }

    public static int RunHelper() {
        string stage = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        bool ready = false;
        try {
            string parentDir = Path.GetDirectoryName(stage);
            if (!IsStage(stage, parentDir)) throw new InvalidDataException("更新目录无效。");
            var plan = new JavaScriptSerializer().Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(stage, "update.json")));
            if (plan == null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(plan.Target)), parentDir, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(plan.Target), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新目标无效。");
            Version version = ParseVersion(plan.Version);
            if (version <= new Version(AppVersion.Number)) throw new InvalidDataException("已拒绝安装相同或更旧版本。");
            string payload = Path.Combine(stage, "payload.exe");
            VerifyFile(payload, plan.Size, plan.Sha256);
            VerifyProgram(payload, version);
            // One replacement at a time. Release the lock automatically when the helper exits.
            using (var updateLock = new FileStream(plan.Target + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            using (var parent = Process.GetProcessById(plan.ParentId)) {
                ValidateHash(plan.OriginalSha256);
                if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted ||
                    !string.Equals(parent.MainModule.FileName, plan.Target, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(HashFile(plan.Target), plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("原程序进程已改变，请重新检查更新。");
                File.WriteAllText(Path.Combine(stage, "ready"), "ready");
                ready = true;
                var watch = Stopwatch.StartNew();
                while (!parent.WaitForExit(200)) {
                    if (File.Exists(Path.Combine(stage, "cancel"))) return 1;
                    if (watch.ElapsedMilliseconds > 60000) throw new TimeoutException("原程序未退出，更新已取消。");
                }
                if (File.Exists(Path.Combine(stage, "cancel"))) return 1;
                VerifyFile(payload, plan.Size, plan.Sha256);
                if (!string.Equals(HashFile(plan.Target), plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("程序文件已被其他操作修改，更新已停止。");
                string backup = Path.Combine(stage, "previous.exe");
                try {
                    ReplaceAndRestart(payload, plan.Target, backup, delegate(string target) {
                        using (var started = Process.Start(new ProcessStartInfo { FileName = target,
                            WorkingDirectory = parentDir, UseShellExecute = false })) {
                            if (started == null || started.WaitForExit(3000))
                                throw new IOException("新版程序启动后提前退出。");
                        }
                    });
                } catch {
                    // Only restart the original if it is still present (or rollback succeeded).
                    if (File.Exists(plan.Target) && string.Equals(HashFile(plan.Target), plan.OriginalSha256, StringComparison.OrdinalIgnoreCase)) {
                        try { Process.Start(new ProcessStartInfo { FileName = plan.Target, WorkingDirectory = parentDir, UseShellExecute = true }); } catch { }
                    }
                    throw;
                }
                File.WriteAllText(Path.Combine(stage, "result.txt"), "Updated to " + plan.Version + ". Previous executable: " + backup);
                // Keep the stage, original executable and result for manual recovery.
            }
            return 0;
        } catch (Exception ex) {
            try { File.WriteAllText(Path.Combine(stage, "error.txt"), ex.Message, Encoding.UTF8); } catch { }
            if (ready) MessageBox.Show("更新未完成：" + ex.Message + "\r\n原程序备份和详细信息位于：\r\n" + stage,
                "MiVoiceMic 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    public static string FriendlyError(Exception ex) {
        if (ex is UnauthorizedAccessException) return "无法写入程序目录，请以管理员身份运行后重试。";
        if (ex is WebException) return "无法连接 GitHub，请检查网络后重试。";
        if (ex is OperationCanceledException) return "已取消更新。";
        return ex.Message;
    }
}
