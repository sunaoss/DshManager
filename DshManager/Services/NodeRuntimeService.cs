using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DshManager.Services;

/// <summary>
/// Node.js 便携版运行时服务：从 nodejs.org 下载官方 Windows x64 zip，解压到面板自己的数据目录
/// （%LOCALAPPDATA%\DshManager\runtimes\node），供面板启动 dsh 时使用。
///
/// 【为什么用便携版而不是官方安装包】
/// 1) 官方 .msi 需要管理员权限、会写注册表并修改系统 PATH；
/// 2) 用户明确不希望程序擅自改动系统环境，下载/安装必须由用户点击触发；
/// 3) 便携版 zip 解压即用，卸载只需删目录，也不会影响系统里已有的 Node（不抢 PATH 优先级）。
///
/// 【安全边界】只写"面板数据目录下的 runtimes\node"与系统临时目录；
/// 不碰系统 PATH、不写注册表、不安装系统级 Node、不触碰用户 .dsh 目录。
/// </summary>
public class NodeRuntimeService
{
    /// <summary>网络不可用时的内置兜底 LTS 版本（本机实测该版本 zip 可正常下载）。</summary>
    public const string FallbackLtsVersion = "v24.12.0";

    /// <summary>Node 官方版本索引：一个 JSON 数组，每项含 version 与 lts 标记。</summary>
    private const string VersionIndexUrl = "https://nodejs.org/dist/index.json";

    /// <summary>HTTP 超时：便携版 zip 约 35 MB，慢网也要给足时间，放宽到 10 分钟。</summary>
    private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(10);

    /// <summary>验证 node.exe 可执行的超时（毫秒）：超过即视为安装失败。</summary>
    private const int VerifyTimeoutMs = 15_000;

    /// <summary>流式下载缓冲区大小（约 80 KB）。</summary>
    private const int DownloadBufferSize = 80 * 1024;

    /// <summary>生产环境共享的 HttpClient（静态单例，避免反复创建导致连接耗尽）。</summary>
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private readonly HttpClient _http;

    /// <summary>zip 解压委托（zip 路径, 目标目录）；默认走 System.IO.Compression，测试可注入假实现。</summary>
    private readonly Action<string, string> _extractZip;

    /// <summary>node.exe 版本探测委托；默认真实执行 node --version，测试可注入假实现。</summary>
    private readonly Func<string, string?> _probeNodeVersion;

    /// <summary>系统 Node 探测委托（PATH 里是否有 node）；默认真实执行 where.exe，测试可注入固定值。</summary>
    private readonly Func<bool> _systemNodeProbe;

    /// <summary>
    /// 创建服务实例。
    /// </summary>
    /// <param name="runtimesRoot">便携版安装根目录；为空时取 %LOCALAPPDATA%\DshManager\runtimes\node。</param>
    /// <param name="messageHandler">HTTP 处理器；为空时使用静态共享 HttpClient（测试注入假 handler 以避免真实联网/下载）。</param>
    /// <param name="zipExtractor">zip 解压委托；为空时使用真实解压（测试注入假实现以避免真的解压安装）。</param>
    /// <param name="nodeVersionProbe">node.exe 版本探测委托；为空时真实执行 node --version（测试注入假实现）。</param>
    /// <param name="systemNodeProbe">系统 Node 探测委托；为空时真实执行 where node（测试注入固定值以免受本机 PATH 影响）。</param>
    public NodeRuntimeService(
        string? runtimesRoot = null,
        HttpMessageHandler? messageHandler = null,
        Action<string, string>? zipExtractor = null,
        Func<string, string?>? nodeVersionProbe = null,
        Func<bool>? systemNodeProbe = null)
    {
        RuntimesRoot = string.IsNullOrWhiteSpace(runtimesRoot) ? GetDefaultRuntimesRoot() : runtimesRoot;

        // 测试注入假 handler 时用实例 HttpClient；生产复用静态单例
        _http = messageHandler is null
            ? SharedHttpClient
            : new HttpClient(messageHandler) { Timeout = HttpClientTimeout };

        _extractZip = zipExtractor
            ?? ((zipPath, destDir) => ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true));
        _probeNodeVersion = nodeVersionProbe ?? ProbeNodeVersion;
        _systemNodeProbe = systemNodeProbe ?? IsSystemNodeAvailable;
    }

    /// <summary>便携版安装根目录：&lt;数据目录&gt;\runtimes\node。</summary>
    public string RuntimesRoot { get; }

    /// <summary>
    /// 已安装的便携版 node.exe 绝对路径；未安装（或文件不存在）返回 null。
    /// 必须真实存在才返回，避免"目录在但文件被删"时误判为已安装。
    /// </summary>
    public string? GetPortableNodePath()
    {
        var path = Path.Combine(RuntimesRoot, "node.exe");
        return SafeFileExists(path) ? path : null;
    }

    /// <summary>便携版 npm.cmd 绝对路径；未安装返回 null。</summary>
    public string? GetPortableNpmPath()
    {
        var path = Path.Combine(RuntimesRoot, "npm.cmd");
        return SafeFileExists(path) ? path : null;
    }

    /// <summary>
    /// 启动 dsh 时应使用的 node 路径。
    /// **策略：系统已有环境优先**（用户明确要求）。原因：dsh 运行时会调用系统里的
    /// shell / 原生模块 / git 等外部命令，若改用面板便携版启动，这些子进程的环境会与系统不一致。
    /// 返回 null 表示「保持裸 node」，即沿用系统 PATH 里的 Node；
    /// 仅当系统完全找不到 Node 时，才回退到面板便携版（保证「完全没有 Node 时也能跑起来」）。
    /// </summary>
    public string? GetPreferredNodePath() => _systemNodeProbe() ? null : GetPortableNodePath();

    /// <summary>
    /// PATH 里是否存在可用的 node（用 where.exe 查询，不启动 node 进程；任何异常一律视为不存在）。
    /// </summary>
    private static bool IsSystemNodeAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null) return false;

            // 【为什么必须先异步读、再等退出】where.exe 正常会立刻退出，但一旦它卡住
            // （杀软扫描、网络盘 PATH 项），同步的 ReadToEnd() 会一直阻塞在管道上，
            // 排在它后面的 WaitForExit(5000) 永远轮不到执行 ⇒ 超时形同虚设。
            // 先发起异步读，超时判定才真正生效；超时后整树 kill 让读取自然结束。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                return false;
            }

            process.WaitForExit(); // 排空异步输出
            var output = stdout.Result;
            _ = stderr.Result; // 必须排空 stderr，否则子进程写满 stderr 缓冲会死锁

            // where 命中会输出路径行并以 0 退出；未命中则非 0
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取远程版本索引取最新 LTS 版本号（形如 v24.12.0）；
    /// 网络失败 / 索引格式异常时回退到内置安全版本，保证离线环境下仍可完成安装。
    /// </summary>
    public async Task<string?> GetLatestLtsVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var indexJson = await FetchVersionIndexAsync(ct).ConfigureAwait(false);
            var version = ParseLatestLtsVersion(indexJson);
            if (!string.IsNullOrWhiteSpace(version))
                return version;
        }
        catch
        {
            // 网络不通 / JSON 解析失败：一律走内置兜底版本（见 FallbackLtsVersion）
        }
        return FallbackLtsVersion;
    }

    /// <summary>
    /// 下载并安装最新 LTS 便携版 Node：zip 解压后把内容规整到 <see cref="RuntimesRoot"/>，
    /// 最后运行一次 node --version 验证可执行。onProgress 回传中文进度（下载/解压/验证）。
    /// 任何失败都返回 false 并给出中文原因，不抛异常（除取消外的异常被吞并转成进度文本）。
    /// </summary>
    public async Task<bool> InstallPortableNodeAsync(Action<string> onProgress, CancellationToken ct = default)
    {
        var report = onProgress ?? (_ => { });
        string? workRoot = null;

        try
        {
            report("正在获取 Node.js 最新 LTS 版本…");
            // 网络失败时 GetLatestLtsVersionAsync 会回退内置版本 → 离线也能装
            var version = await GetLatestLtsVersionAsync(ct).ConfigureAwait(false) ?? FallbackLtsVersion;
            report($"目标版本：Node {version}（Windows x64 便携版）");

            // 全程在临时目录作业，中途失败不会污染正式安装目录
            workRoot = Path.Combine(Path.GetTempPath(), "DshManager", "node-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workRoot);
            var partPath = Path.Combine(workRoot, "node.zip.part");
            var zipPath = Path.Combine(workRoot, "node.zip");

            await DownloadToFileAsync(BuildDownloadUrl(version), partPath, version, report, ct).ConfigureAwait(false);
            // 下载完成后才改名：避免把半成品 zip 当成成品去解压
            File.Move(partPath, zipPath, overwrite: true);

            var extractDir = Path.Combine(workRoot, "extract");
            report("正在解压 Node 便携版…");
            _extractZip(zipPath, extractDir);

            // zip 内顶层是 node-vX-win-x64\，需要把它里面的内容搬到 RuntimesRoot 之下
            var payloadDir = ResolvePayloadDirectory(extractDir, version);
            if (payloadDir is null)
            {
                report("❌ 安装失败：解压结果中未找到 node.exe");
                return false;
            }

            StagePayloadIntoRuntimesRoot(payloadDir);

            var nodeExe = GetPortableNodePath();
            if (nodeExe is null)
            {
                report("❌ 安装失败：安装完成后仍未找到 node.exe");
                return false;
            }

            // 真跑一次 --version 验证确实可执行，避免"下到坏包却看起来装好了"
            var probedVersion = _probeNodeVersion(nodeExe);
            if (string.IsNullOrWhiteSpace(probedVersion))
            {
                report("❌ 安装失败：便携版 node.exe 无法执行（文件可能损坏）");
                return false;
            }

            report($"✅ Node 便携版安装完成：{nodeExe}（{probedVersion.Trim()}）");
            return true;
        }
        catch (OperationCanceledException)
        {
            report("⚠️ 安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            report($"❌ 安装失败：{ex.Message}");
            return false;
        }
        finally
        {
            // 临时文件无论成败都清理，避免长期占用磁盘
            if (workRoot is not null)
            {
                try { Directory.Delete(workRoot, recursive: true); }
                catch { /* 清理失败不影响安装结论 */ }
            }
        }
    }

    // ==================== 版本获取与回退 ====================

    /// <summary>拉取官方版本索引的原始 JSON；非 2xx 返回 null（由调用方走兜底）。</summary>
    private async Task<string?> FetchVersionIndexAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(VersionIndexUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 index.json 中挑出最新的 LTS 版本号（形如 v24.12.0）。
    /// lts 字段为 false 表示非 LTS，字符串（如 "Krypton"）表示 LTS 代号，只认后者；
    /// index.json 已按新到旧排列，但仍显式比较版本号，避免上游排列变化导致取错。
    /// </summary>
    internal static string? ParseLatestLtsVersion(string? indexJson)
    {
        if (string.IsNullOrWhiteSpace(indexJson)) return null;

        using var document = JsonDocument.Parse(indexJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? best = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            // lts 为字符串才算 LTS；false / null / 缺失一律跳过（ValueKind 判定天然覆盖）
            if (!entry.TryGetProperty("lts", out var lts) || lts.ValueKind != JsonValueKind.String)
                continue;
            if (!entry.TryGetProperty("version", out var versionNode))
                continue;

            var version = versionNode.GetString();
            if (string.IsNullOrWhiteSpace(version) || !version.StartsWith('v'))
                continue;

            if (best is null || CompareVersions(version, best) > 0)
                best = version;
        }
        return best;
    }

    /// <summary>把 vX.Y.Z 形式版本号拆成三段整数做比较（非数字段按 0 处理，不抛异常）。</summary>
    private static int CompareVersions(string left, string right)
    {
        var (majorL, minorL, patchL) = ParseVersion(left);
        var (majorR, minorR, patchR) = ParseVersion(right);

        if (majorL != majorR) return majorL.CompareTo(majorR);
        if (minorL != minorR) return minorL.CompareTo(minorR);
        return patchL.CompareTo(patchR);

        static (int Major, int Minor, int Patch) ParseVersion(string value)
        {
            var parts = value.TrimStart('v').Split('.');
            return (ToInt(parts, 0), ToInt(parts, 1), ToInt(parts, 2));
        }

        static int ToInt(string[] parts, int index)
            => index < parts.Length && int.TryParse(parts[index], out var number) ? number : 0;
    }

    // ==================== 下载 ====================

    /// <summary>下载地址遵循官方发行目录结构。</summary>
    private static string BuildDownloadUrl(string version)
        => $"https://nodejs.org/dist/{version}/node-{version}-win-x64.zip";

    /// <summary>
    /// 流式下载到指定文件，并按百分比回报进度（形如「正在下载 Node v24.12.0 … 12.3 MB / 34.7 MB (35%)」）。
    /// 用 ResponseHeadersRead + 循环拷贝而非一次性读入内存：35 MB 文件不占内存，且能实时回报进度。
    /// </summary>
    private async Task DownloadToFileAsync(
        string url, string destPath, string version, Action<string> report, CancellationToken ct)
    {
        var label = $"Node {version}";
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // ContentLength 可能为 null（分块传输）：此时退化为按整 MB 回报
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true);

        var buffer = new byte[DownloadBufferSize];
        long received = 0;
        var lastPercent = -1; // 上次已回报的百分比
        long lastMb = -1;     // 无 Content-Length 时上次已回报的整 MB

        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            if (total is > 0)
            {
                var percent = (int)(received * 100 / total.Value);
                if (percent == lastPercent) continue; // 百分比没变化就不刷屏
                lastPercent = percent;
                report($"正在下载 {label} … {FormatMb(received)} / {FormatMb(total.Value)} ({percent}%)");
            }
            else
            {
                var mb = received / (1024 * 1024);
                if (mb == lastMb) continue;
                lastMb = mb;
                report($"正在下载 {label} … {FormatMb(received)}");
            }
        }

        // 收尾补一条 100%（最后一次写入可能没有触发百分比变化）
        if (total is > 0 && lastPercent < 100)
            report($"正在下载 {label} … {FormatMb(received)} / {FormatMb(total.Value)} (100%)");
    }

    /// <summary>字节数格式化为 MB（保留 1 位小数）。</summary>
    private static string FormatMb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";

    // ==================== 解压后目录规整 ====================

    /// <summary>
    /// 在解压结果里定位真正包含 node.exe 的目录：
    /// 官方 zip 的稳定布局是 &lt;extract&gt;\node-vX-win-x64\node.exe，
    /// 但也递归兜底找一次，避免上游打包布局微调导致安装失败。
    /// </summary>
    private static string? ResolvePayloadDirectory(string extractDir, string version)
    {
        var expected = Path.Combine(extractDir, $"node-{version}-win-x64");
        if (SafeFileExists(Path.Combine(expected, "node.exe")))
            return expected;

        try
        {
            var found = Directory.EnumerateFiles(extractDir, "node.exe", SearchOption.AllDirectories).FirstOrDefault();
            return found is null ? null : Path.GetDirectoryName(found);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把解压出的内容搬到 <see cref="RuntimesRoot"/>（最终保证 RuntimesRoot\node.exe 存在）：
    /// 先清空旧目录保证干净，再优先整体改名（同盘最快），跨盘失败时退化为递归复制。
    /// </summary>
    private void StagePayloadIntoRuntimesRoot(string payloadDir)
    {
        var parent = Path.GetDirectoryName(RuntimesRoot);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        // 清空旧安装目录：避免上一次残留的旧版本文件与新版本混在一起
        if (Directory.Exists(RuntimesRoot))
            Directory.Delete(RuntimesRoot, recursive: true);

        try
        {
            Directory.Move(payloadDir, RuntimesRoot);
        }
        catch
        {
            // 临时目录与数据目录不在同一卷时 Move 会失败 → 逐文件复制
            CopyDirectory(payloadDir, RuntimesRoot);
        }
    }

    /// <summary>递归复制目录（跨卷搬运回退方案）。</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (var childDir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(childDir, Path.Combine(destDir, Path.GetFileName(childDir)));
    }

    // ==================== 安装后验证 / 路径工具 ====================

    /// <summary>
    /// 真实执行 `node.exe --version` 确认是可执行文件；带 15 秒超时，失败 / 超时返回 null。
    /// 直接以 exe 路径启动（Windows 上 node.exe 无需经 cmd 解析）。
    /// </summary>
    private static string? ProbeNodeVersion(string nodeExePath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = nodeExePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (!process.Start()) return null;

            // 异步读走管道避免死锁；版本号很短，超时兜底即可
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(VerifyTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                return null;
            }
            process.WaitForExit();

            var text = stdout.Result.Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            // 文件损坏 / 无执行权限等：统一视为验证失败
            return null;
        }
    }

    /// <summary>
    /// 默认便携版根目录：%LOCALAPPDATA%\DshManager\runtimes\node；
    /// 取不到 LocalApplicationData 时回退到程序目录 runtimes\node（保证总有可用路径）。
    /// </summary>
    private static string GetDefaultRuntimesRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "runtimes", "node")
            : Path.Combine(localAppData, "DshManager", "runtimes", "node");
    }

    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    /// <summary>创建生产用共享 HttpClient：跟随重定向、超时 10 分钟、固定 User-Agent。</summary>
    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = HttpClientTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DshManager/1.0");
        return client;
    }
}
