using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeskApp.Utils;

/// <summary>GitHub Release 上发现的一个可安装版本</summary>
public sealed record UpdateInfo(
    Version Version,
    string VersionText,
    string Tag,
    string Notes,
    string DownloadUrl,
    string ApiDownloadUrl,
    string AssetName,
    DateTimeOffset PublishedAt,
    long Size);

/// <summary>
/// 在线更新：GitHub Releases latest → 下载 .zip 附件 → 解压到 updates\&lt;版本&gt;\app
/// → 用「更新进程」（从解压目录启动的程序副本）把文件覆盖回程序目录 → 重启主程序。
///
/// 为什么需要一个更新进程：Windows 会锁定正在运行的程序集 DLL，主进程自己覆盖不了。
/// 从解压目录启动的副本不占用原目录的 DLL，等主进程退出后即可安全覆盖。
/// 全程只用自身 exe + 文件复制，不启动任何脚本/外部 shell，避免安全软件误报。
/// </summary>
public static class UpdateService
{
    public const string Repository = "zhaosai6869/deskTool";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string UpdaterArg = "--apply-update";

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub API 强制要求 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskApp-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    });

    public static Version CurrentVersion =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentVersionText => Format(CurrentVersion);

    /// <summary>
    /// 检查最新版本。返回 null 表示仓库还没有发布过 Release；
    /// 有 Release 但没有 .zip 附件时抛出带说明的异常。
    /// </summary>
    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        using var response = await Http.Value.GetAsync(LatestApi, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // 还没有任何 Release
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        var root = doc.RootElement;

        var tag = Str(root, "tag_name");
        if (!TryParseVersion(tag, out var version))
        {
            return null;
        }

        string? url = null;
        string? apiUrl = null;
        string? asset = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assets.EnumerateArray())
            {
                var name = Str(item, "name");
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                url = Str(item, "browser_download_url");
                // api.github.com 资产端点，作为 github.com 直链被代理掐断时的备用地址
                apiUrl = Str(item, "url");
                asset = name;
                if (item.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var bytes))
                {
                    size = bytes;
                }

                break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("最新版本没有附带 .zip 更新包，请先上传发布附件。");
        }

        var published = DateTimeOffset.Now;
        if (root.TryGetProperty("published_at", out var pub) &&
            DateTimeOffset.TryParse(pub.GetString(), out var parsed))
        {
            published = parsed;
        }

        return new UpdateInfo(version, Format(version), tag, Str(root, "body"),
            url!, apiUrl ?? string.Empty, asset ?? "update.zip", published, size);
    }

    /// <summary>下载并解压更新包，返回可直接运行的「更新进程」所在目录</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, Action<string> report, CancellationToken ct = default)
    {
        var baseDir = BaseDir;
        var versionDir = Path.Combine(baseDir, "updates", info.VersionText);
        if (Directory.Exists(versionDir))
        {
            TryDeleteDirectory(versionDir);
        }

        var stage = Path.Combine(versionDir, "app");
        Directory.CreateDirectory(stage);
        var zipPath = Path.Combine(versionDir, "package.zip");

        try
        {
            await DownloadPackageAsync(info, zipPath, report, ct).ConfigureAwait(false);

            report("正在解压并校验更新包…");
            ZipFile.ExtractToDirectory(zipPath, stage, overwriteFiles: true);
            var root = FindPayloadRoot(stage);
            if (!File.Exists(Path.Combine(root, "DeskApp.exe")))
            {
                throw new InvalidOperationException("更新包里没有找到 DeskApp.exe，请检查发布附件内容。");
            }

            TryDeleteFile(zipPath);
            report("更新包已就绪。");
            return root;
        }
        catch
        {
            TryDeleteDirectory(versionDir);
            throw;
        }
    }

    /// <summary>
    /// 下载更新包。browser_download_url 指向 github.com，在部分代理 / 安全软件环境下会被掐断，
    /// 失败时自动回退到 api.github.com 的资产端点（必须声明 Accept: application/octet-stream）。
    /// 下载过程按百分比回报，避免慢速网络下看起来像卡死。
    /// </summary>
    private static async Task DownloadPackageAsync(
        UpdateInfo info, string zipPath, Action<string> report, CancellationToken ct)
    {
        var urls = new List<(string Url, bool IsApi)>();
        if (!string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            urls.Add((info.DownloadUrl, false));
        }

        if (!string.IsNullOrWhiteSpace(info.ApiDownloadUrl))
        {
            urls.Add((info.ApiDownloadUrl, true));
        }

        if (urls.Count == 0)
        {
            throw new InvalidOperationException("更新包缺少下载地址。");
        }

        for (var i = 0; i < urls.Count; i++)
        {
            var (url, isApi) = urls[i];
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (isApi)
                {
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.ParseAdd("application/octet-stream");
                }

                using var response = await Http.Value
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? info.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
                                 FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    var milestone = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        if (total <= 0)
                        {
                            continue;
                        }

                        var percent = (int)(done * 100 / total);
                        if (percent < milestone + 10)
                        {
                            continue;
                        }

                        milestone = percent / 10 * 10;
                        report($"正在下载更新包… {percent}%" +
                               $"（{done / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB）");
                    }

                    await target.FlushAsync(ct).ConfigureAwait(false);
                }

                var actual = new FileInfo(zipPath).Length;
                if (info.Size > 0 && actual != info.Size)
                {
                    throw new IOException($"更新包不完整（{actual} / {info.Size} 字节）。");
                }

                return;
            }
            catch when (i < urls.Count - 1)
            {
                TryDeleteFile(zipPath);
                report("下载地址不可用，正在尝试备用地址…");
            }
        }
    }

    /// <summary>启动解压目录里的程序副本执行替换，然后关闭当前主程序</summary>
    public static void LaunchUpdaterAndExit(string stageDir)
    {
        var updater = Path.Combine(stageDir, "DeskApp.exe");
        if (!File.Exists(updater))
        {
            throw new FileNotFoundException("更新进程缺失", updater);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = updater,
            WorkingDirectory = stageDir,
            UseShellExecute = true,
            Arguments = $"{UpdaterArg} \"{stageDir}\" \"{BaseDir}\" {Environment.ProcessId}"
        });

        Application.Current.Shutdown();
    }

    /// <summary>
    /// 更新进程入口（App.OnStartup 调用）：等主进程退出 → 覆盖文件 → 拉起新版 → 自身退出。
    /// 返回 true 表示本次启动是更新进程，调用方应立即 Shutdown。
    /// </summary>
    public static bool HandleUpdaterArgs(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], UpdaterArg, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length < 3)
        {
            return true;
        }

        var stage = args[1].Trim('"');
        var baseDir = args[2].Trim('"');
        var pid = args.Length >= 4 && int.TryParse(args[3], out var value) ? value : 0;

        try
        {
            if (pid > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    parent.WaitForExit(15000);
                }
                catch
                {
                    // 主进程已退出，直接继续
                }
            }
            else
            {
                Thread.Sleep(1200);
            }

            CopyDirectory(stage, baseDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(baseDir, "DeskApp.exe"),
                WorkingDirectory = baseDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"更新应用失败：{ex.Message}\n\n可手动把下面目录里的文件复制到程序目录：\n{stage}",
                "桌面工具 · 更新", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }

        return true;
    }

    /// <summary>主程序启动时清理两天前的旧更新目录（跳过更新进程自身所在目录）</summary>
    public static void CleanupOldUpdates()
    {
        try
        {
            var root = Path.Combine(BaseDir, "updates");
            if (!Directory.Exists(root))
            {
                return;
            }

            var self = BaseDir;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (self.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-2))
                {
                    TryDeleteDirectory(dir);
                }
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    private static string BaseDir => AppContext.BaseDirectory
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>兼容 zip 里多包一层目录（如 DeskApp-v1.0.1/…）的情况</summary>
    private static string FindPayloadRoot(string stage)
    {
        var current = stage;
        for (var i = 0; i < 3; i++)
        {
            if (File.Exists(Path.Combine(current, "DeskApp.exe")))
            {
                return current;
            }

            var dirs = Directory.GetDirectories(current);
            var files = Directory.GetFiles(current);
            if (dirs.Length == 1 && files.Length == 0)
            {
                current = dirs[0];
                continue;
            }

            break;
        }

        return stage;
    }

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            text = text[..dash];
        }

        if (Version.TryParse(text, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    /// <summary>补齐到 4 段，避免 1.0.1（3 段）被当成比 1.0.0.0 小</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static string Format(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 旧进程可能还占着文件，留到下次启动再清
        }
    }
}
