using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SkyTool.Common;

public class UpdateInfo
{
    public string Version { get; set; }
    public string Url { get; set; }
    public string Sha256 { get; set; }
    public string Notes { get; set; }
    public string Date { get; set; }
}

/// <summary>检查 / 下载 / 应用更新。清单托管在下载站，零额外依赖
/// （HttpClient + System.Text.Json 均为 .NET 运行时自带，安装包不增大）。</summary>
public static class UpdateService
{
    // 按版本走各自的清单：Pro 用 latest.json（与历史 1.0.0 用户兼容），Lite 用 latest-lite.json，
    // 这样极简版用户只会被推极简版、Pro 用户只会被推 Pro，互不串包。
#if OCR
    private const string ManifestUrl = "http://124.223.55.175/sky-tool/latest.json";
#else
    private const string ManifestUrl = "http://124.223.55.175/sky-tool/latest-lite.json";
#endif

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyTool", "update.json");

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>拉取版本清单；失败返回 null。</summary>
    public static async Task<UpdateInfo> FetchAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(ManifestUrl);
            return JsonSerializer.Deserialize<UpdateInfo>(json, JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>清单版本是否比当前版本新。</summary>
    public static bool IsNewer(string remote)
        => Version.TryParse(remote, out var r)
           && Version.TryParse(CurrentVersion, out var c)
           && r > c;

    /// <summary>每天至多自动检查一次：今天还没查过则返回 true 并记下今天。</summary>
    public static bool ShouldAutoCheck()
    {
        try
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (File.Exists(StateFile))
            {
                var st = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StateFile));
                if (st != null && st.TryGetValue("lastCheck", out var last) && last == today)
                    return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
            File.WriteAllText(StateFile,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["lastCheck"] = today }));
            return true;
        }
        catch { return true; }
    }

    /// <summary>下载新版到临时文件并校验 SHA256，返回新文件路径；失败抛异常。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string newExe = Path.Combine(dir, "SkyTool.new.exe");

        using var resp = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(newExe))
        using (var sha = SHA256.Create())
        {
            var buf = new byte[81920];
            long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                string got = Convert.ToHexString(sha.Hash).ToLowerInvariant();
                if (!string.Equals(got, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("下载校验失败（SHA256 不匹配），已取消更新。");
            }
        }
        return newExe;
    }

    /// <summary>用新文件替换正在运行的 exe 并重启（Windows 允许给运行中的 exe 改名）。</summary>
    public static void ApplyAndRestart(string newExe)
    {
        string cur = Environment.ProcessPath;
        string dir = Path.GetDirectoryName(cur);
        string old = Path.Combine(dir, "SkyTool.old.exe");

        try { if (File.Exists(old)) File.Delete(old); } catch { /* 上次的残留还被占用，忽略 */ }

        File.Move(cur, old);          // 把正在运行的 exe 改名腾出原路径
        try
        {
            File.Move(newExe, cur);   // 新版就位
        }
        catch
        {
            try { File.Move(old, cur); } catch { } // 失败回滚，保证原 exe 还在
            throw;
        }

        System.Diagnostics.Process.Start(cur);
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>启动时清理上次更新遗留的旧 exe（老进程退出后才删得掉，后台重试几次）。</summary>
    public static void CleanupOld()
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir == null) return;
        string old = Path.Combine(dir, "SkyTool.old.exe");
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (File.Exists(old)) File.Delete(old); return; }
                catch { await Task.Delay(500); }
            }
        });
    }
}
