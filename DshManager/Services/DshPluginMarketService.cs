using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>
/// 插件市场搜索的一页结果。
/// <see cref="Error"/> 非空表示本次请求失败（网络 / 解析），界面据此展示中文错误提示，绝不抛异常。
/// </summary>
public class PluginMarketSearchResult
{
    /// <summary>本页条目（已按 npm 返回顺序）。</summary>
    public IReadOnlyList<PluginMarketItem> Items { get; init; } = Array.Empty<PluginMarketItem>();

    /// <summary>匹配总数（用于分页；失败时为 0）。</summary>
    public int Total { get; init; }

    /// <summary>失败原因（中文）；成功时为 null。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 市场列表的排序方式。
/// 【为什么排序在客户端做】实测 npm search 的 score.detail（popularity / quality / maintenance）
/// 对所有包都恒为 1，用权重重排也不改变顺序，因此服务端排序不可用；「更新时间 / 下载量」
/// 只能一次性拉宽（250 条）后在本地排序。
/// </summary>
public enum MarketSortMode
{
    /// <summary>相关度：保持 npm 服务端返回顺序（服务端分页）。</summary>
    Relevance,

    /// <summary>更新时间（最新）：按发布时间降序，无发布时间的排最后。</summary>
    UpdatedTime,

    /// <summary>下载量（最多）：按最近一周下载量降序。</summary>
    Downloads,
}

/// <summary>
/// 联网的中文插件市场服务。
///
/// 【数据源】npm registry 官方 search 接口（实测 total 六千余，可用）：
///   GET {registry}/-/v1/search?text=keywords:dsh-plugin&amp;size=N&amp;from=offset
/// 返回 objects[].package 只含英文/通用元数据，**不含 dshhub**（中文简介、分类、权限都在包内 package.json），
/// 因此详情需另发一次 GET {registry}/{name}/latest 拉该版本完整 package.json 再解析 dshhub 段。
/// 注意：registry.npmmirror.com 的 search 接口实测 total=0，不可用于搜索（仅 tarball 下载可加速），故默认走官方源。
///
/// 【为什么用静态单例 HttpClient】每次请求新建 HttpClient 会让连接停留在 TIME_WAIT，
/// 高并发下耗尽本地端口；统一复用单例并设置 User-Agent（npm 要求）与 45 秒超时。
///
/// 【安装 / 卸载为什么用 node + bin.js】本机 dsh 命令是坏 shim（指向不存在的路径），
/// 必须 `node "&lt;DSH_HOME&gt;\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js" plugin --profile &lt;profile&gt; add/remove ...`；
/// 从 registry 安装（而非本地目录）时 dsh.bundle.patch 会自动入 bundles 并生效，是官方正确方式。
///
/// 【异常安全】所有对外方法都不抛异常：网络失败返回空结果 + 中文 Error，卸载/安装失败返回 false 并逐行回传输出。
/// </summary>
public class DshPluginMarketService
{
    /// <summary>默认 registry 源（官方源；镜像源的 search 接口不可用）。</summary>
    public const string DefaultRegistryBase = "https://registry.npmjs.org";

    /// <summary>dsh 自身的包名（用于定位 bin.js 与本机版本）。</summary>
    private const string DshPackageName = "@deepseek-ai/dsh";

    /// <summary>补详情时的最大并发数：够快又不至于被 npm 限流。</summary>
    private const int MaxConcurrentDetailRequests = 5;

    /// <summary>单页最大条数（npm search 接口上限 250）。</summary>
    private const int MaxPageSize = 250;

    /// <summary>npm 下载量统计接口的默认域名（与 registry 不同域，故单独常量）。</summary>
    public const string DefaultDownloadsBase = "https://api.npmjs.org";

    /// <summary>
    /// 单批下载量查询的包数上限。
    /// 【为什么分批】下载量接口走 GET 路径拼包名，一次塞 250 个包名会让 URL 超长（服务端 414 / 客户端限制），
    /// 实测单批 60 个稳定；分批后串行请求，避免并发过多被限流。
    /// </summary>
    public const int DownloadsBatchSize = 60;

    /// <summary>
    /// 静态单例 HttpClient：复用连接、避免端口耗尽。
    /// User-Agent 是 npm registry 的硬性要求（缺失会 403），Accept 固定 JSON。
    /// </summary>
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _dshHome;
    private readonly string _profileName;

    /// <summary>插件命令超时（pnpm 安装耗时较长，给足 5 分钟）。</summary>
    private readonly TimeSpan _commandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 创建市场服务。
    /// </summary>
    /// <param name="registryBase">npm registry 地址；为空时用官方源。</param>
    /// <param name="dshHome">DSH_HOME；为空时用 DshDiscoveryService.GetDefaultDshHome()。</param>
    /// <param name="profileName">目标 profile（对应 &lt;DSH_HOME&gt;\profiles\&lt;name&gt;）；为空时用 "web"。</param>
    public DshPluginMarketService(string? registryBase = null, string? dshHome = null, string? profileName = null)
    {
        RegistryBase = string.IsNullOrWhiteSpace(registryBase) ? DefaultRegistryBase : registryBase.TrimEnd('/');
        _dshHome = string.IsNullOrWhiteSpace(dshHome) ? DshDiscoveryService.GetDefaultDshHome() : dshHome;
        _profileName = string.IsNullOrWhiteSpace(profileName) ? "web" : profileName;
    }

    /// <summary>当前 registry 源地址（不含尾部斜杠）。</summary>
    public string RegistryBase { get; }

    /// <summary>当前操作的 profile 名（界面展示用）。</summary>
    public string ProfileName => _profileName;

    /// <summary>profile 目录：&lt;DSH_HOME&gt;\profiles\&lt;name&gt;（读已安装清单用）。</summary>
    public string ProfileDirectory => Path.Combine(_dshHome, "profiles", _profileName);

    /// <summary>
    /// dsh 入口脚本 &lt;DSH_HOME&gt;\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js。
    /// 之所以不用 dsh 命令本身：本机 dsh shim 是坏的，必须 node 直跑 bin.js。
    /// </summary>
    public string DshBinJsPath => Path.Combine(_dshHome, "profiles", "node_modules", DshPackageName, "lib", "bin.js");

    // ==================== 搜索 ====================

    /// <summary>
    /// 按关键词搜索插件（空关键词 = 浏览全部，用 keywords:dsh-plugin 限定只出 dsh 插件）。
    /// 返回该页条目（不含 dshhub 详情，需另行 EnrichAsync 或 GetDetailAsync 补全）。
    /// </summary>
    public async Task<PluginMarketSearchResult> SearchAsync(string? query, int page, int pageSize, CancellationToken ct = default)
    {
        var url = BuildSearchUrl(RegistryBase, query, page, pageSize);
        try
        {
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            return ParseSearchResult(json);
        }
        catch (OperationCanceledException)
        {
            return new PluginMarketSearchResult { Error = "请求已取消或超时，请稍后重试" };
        }
        catch (HttpRequestException ex)
        {
            return new PluginMarketSearchResult { Error = $"网络请求失败：{ex.Message}，请检查网络或稍后重试" };
        }
        catch (JsonException ex)
        {
            return new PluginMarketSearchResult { Error = $"响应解析失败：{ex.Message}，请稍后重试" };
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常都转为中文错误，绝不冒泡到界面
            return new PluginMarketSearchResult { Error = $"搜索失败：{ex.Message}" };
        }
    }

    /// <summary>用当前 registry 源构造 search 请求 URL（实例版本，供界面 / 测试直接调用）。</summary>
    public string BuildSearchUrl(string? query, int page, int pageSize)
        => BuildSearchUrl(RegistryBase, query, page, pageSize);

    /// <summary>
    /// 构造 search 请求 URL（抽成静态方法便于单测，不需要真实联网）。
    /// 关键词处理：空 → text=keywords:dsh-plugin；非空 → text=&lt;已转义关键词&gt;+keywords:dsh-plugin，
    /// 「+」在 npm 语义里是与（AND），这样可保证结果仍是 dsh 插件。
    /// </summary>
    public static string BuildSearchUrl(string registryBase, string? query, int page, int pageSize)
    {
        var baseUrl = string.IsNullOrWhiteSpace(registryBase) ? DefaultRegistryBase : registryBase.TrimEnd('/');
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var safePage = Math.Max(0, page);

        // 空关键词只按关键词过滤；非空时把用户输入转义后与关键词做 AND（冒号在 query 里合法，故保留 keywords:dsh-plugin 原样可读）
        var text = string.IsNullOrWhiteSpace(query)
            ? "keywords:dsh-plugin"
            : $"{Uri.EscapeDataString(query.Trim())}+keywords:dsh-plugin";

        return $"{baseUrl}/-/v1/search?text={text}&size={size}&from={safePage * size}";
    }

    /// <summary>解析 search 响应：objects[].package → 条目（不含 dshhub）。</summary>
    private static PluginMarketSearchResult ParseSearchResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var total = 0;
        if (root.TryGetProperty("total", out var totalElement))
            totalElement.TryGetInt32(out total);

        var items = new List<PluginMarketItem>();
        if (root.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in objects.EnumerateArray())
            {
                if (!obj.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object)
                    continue;
                var item = ParseSearchItem(package);
                if (item is not null) items.Add(item);
            }
        }

        return new PluginMarketSearchResult { Items = items, Total = total };
    }

    /// <summary>把 search 结果里的 package 节点解析为条目。</summary>
    private static PluginMarketItem? ParseSearchItem(JsonElement package)
    {
        var name = ReadString(package, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var item = new PluginMarketItem
        {
            Name = name,
            Version = ReadString(package, "version"),
            Description = ReadString(package, "description"),
            Keywords = ReadStringArray(package, "keywords"),
        };

        var date = ReadString(package, "date");
        if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var published))
        {
            item.PublishedAt = published.UtcDateTime;
        }

        if (package.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            item.NpmUrl = ReadString(links, "npm");
            item.HomepageUrl = ReadString(links, "homepage");
            item.RepositoryUrl = ReadString(links, "repository");
        }

        return item;
    }

    // ==================== 详情 ====================

    /// <summary>
    /// 拉取单个包的完整元数据（GET {registry}/{name}/latest）并解析 dshhub。
    /// 失败（包不存在 / 网络异常 / 解析失败）返回 null，不抛异常。
    /// </summary>
    public async Task<PluginMarketItem?> GetDetailAsync(string packageName, CancellationToken ct = default)
    {
        var name = packageName?.Trim() ?? string.Empty;
        if (name.Length == 0) return null;

        // 作用域包名（@scope/name）充当路径段是 npm registry 的约定；此处刻意不转义，否则 '/' 会变成 %2F 导致 404
        var url = $"{RegistryBase}/{name}/latest";
        try
        {
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            return TryParseDetail(json, name, out var item) ? item : null;
        }
        catch
        {
            // 详情失败只影响该包的补全，返回 null 由调用方跳过
            return null;
        }
    }

    /// <summary>
    /// 为一批条目补 dshhub 详情（限量并发 5，单个失败不影响其它）。
    /// 已带 dshhub 的条目跳过，避免重复请求。
    /// </summary>
    /// <param name="items">待补全的条目（原地修改）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="onProgress">进度回调（已完成数, 总数），界面用来显示「正在获取详情 8/20…」。</param>
    public async Task EnrichAsync(IEnumerable<PluginMarketItem> items, CancellationToken ct = default,
        Action<int, int>? onProgress = null)
    {
        var targets = items?.Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Name) && !i.HasDshHub).ToList()
                      ?? new List<PluginMarketItem>();
        if (targets.Count == 0)
        {
            onProgress?.Invoke(0, 0);
            return;
        }

        // 信号量限制并发：既缩短总耗时，又避免瞬间几十个请求把 npm 打成限流
        using var gate = new SemaphoreSlim(MaxConcurrentDetailRequests);
        var total = targets.Count;
        var done = 0;

        async Task EnrichOneAsync(PluginMarketItem item)
        {
            var acquired = false;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                var detail = await GetDetailAsync(item.Name, ct).ConfigureAwait(false);
                if (detail is not null) CopyMetadata(item, detail);
            }
            catch
            {
                // 单包失败（网络 / 解析 / 取消）静默跳过，不影响其它包
            }
            finally
            {
                if (acquired) gate.Release();
                var current = Interlocked.Increment(ref done);
                onProgress?.Invoke(current, total);
            }
        }

        await Task.WhenAll(targets.Select(EnrichOneAsync)).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析 `/&lt;name&gt;/latest` 响应为条目并提取 dshhub 段。
    /// 抽成公开静态方法便于单测（无需联网）：无 dshhub 时仍返回 true（HasDshHub=false），
    /// 仅 JSON 非法 / 非对象时返回 false。
    /// </summary>
    public static bool TryParseDetail(string json, string packageName, out PluginMarketItem item)
    {
        item = new PluginMarketItem();
        if (string.IsNullOrWhiteSpace(json)) return false;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var name = ReadString(root, "name");
            var parsed = new PluginMarketItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? packageName?.Trim() ?? string.Empty : name,
                Version = ReadString(root, "version"),
                Description = ReadString(root, "description"),
                HomepageUrl = ReadString(root, "homepage"),
                RepositoryUrl = ReadRepositoryUrl(root),
                Keywords = ReadStringArray(root, "keywords"),
            };
            parsed.NpmUrl = parsed.Name.Length > 0 ? $"https://www.npmjs.com/package/{parsed.Name}" : string.Empty;

            // dshhub 段是市场元数据规范；缺省不是错误，只是没有中文简介 / 权限等信息
            if (root.TryGetProperty("dshhub", out var hub) && hub.ValueKind == JsonValueKind.Object)
            {
                parsed.HasDshHub = true;
                parsed.DisplayName = ReadString(hub, "displayName");
                parsed.Summary = ReadString(hub, "summary");
                parsed.Categories = ReadStringArray(hub, "categories");
                parsed.Surfaces = ReadStringArray(hub, "surfaces");
                parsed.Provides = ReadNestedStringArray(hub, "capabilities", "provides");
                parsed.NetworkPermissions = ReadNestedStringArray(hub, "permissions", "network");
                if (hub.TryGetProperty("compatibility", out var compatibility) && compatibility.ValueKind == JsonValueKind.Object)
                {
                    parsed.DshRequirement = ReadString(compatibility, "dsh");
                    parsed.NodeRequirement = ReadString(compatibility, "node");
                }
            }

            item = parsed;
            return true;
        }
    }

    /// <summary>把详情里的 dshhub 与缺失的通用元数据补进目标条目（搜索接口的结果用）。</summary>
    private static void CopyMetadata(PluginMarketItem target, PluginMarketItem detail)
    {
        target.DisplayName = detail.DisplayName;
        target.Summary = detail.Summary;
        target.Categories = detail.Categories;
        target.Surfaces = detail.Surfaces;
        target.Provides = detail.Provides;
        target.NetworkPermissions = detail.NetworkPermissions;
        target.DshRequirement = detail.DshRequirement;
        target.NodeRequirement = detail.NodeRequirement;
        target.HasDshHub = detail.HasDshHub;

        // 搜索接口有时不给全描述/链接，详情里有才补齐（不覆盖已有值）
        if (string.IsNullOrWhiteSpace(target.Description)) target.Description = detail.Description;
        if (string.IsNullOrWhiteSpace(target.RepositoryUrl)) target.RepositoryUrl = detail.RepositoryUrl;
        if (string.IsNullOrWhiteSpace(target.HomepageUrl)) target.HomepageUrl = detail.HomepageUrl;
        if (string.IsNullOrWhiteSpace(target.NpmUrl)) target.NpmUrl = detail.NpmUrl;
        if (target.Keywords.Count == 0) target.Keywords = detail.Keywords;
    }

    // ==================== 已安装清单 ====================

    /// <summary>
    /// 读本机已安装插件：profile 的 package.json 的 dependencies（键=包名）与 dsh.profile.bundles。
    /// 版本优先取 node_modules 里真实包版本，读不到时退回安装规格（去掉 ^~ 前缀）。
    /// 清单缺失 / 损坏时返回已解析到的部分（可能为空），绝不抛异常。
    /// </summary>
    public IReadOnlyDictionary<string, string> GetInstalledPlugins()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(ProfileDirectory, "package.json");
        if (!File.Exists(manifestPath)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in dependencies.EnumerateObject())
                {
                    result[property.Name] = ResolveInstalledVersion(property.Name)
                                            ?? NormalizeVersionSpec(property.Value.GetString() ?? string.Empty);
                }
            }

            // bundles 是 patch 挂载层，可能被手工写入而未进 dependencies，也要算作已安装
            foreach (var bundle in ReadBundles(root))
            {
                if (result.ContainsKey(bundle)) continue;
                result[bundle] = ResolveInstalledVersion(bundle) ?? string.Empty;
            }
        }
        catch
        {
            // 清单损坏（JSON 非法）→ 返回当前已解析部分，界面照常显示
        }
        return result;
    }

    /// <summary>
    /// 读本机 dsh 版本：优先 &lt;DSH_HOME&gt;\profiles\node_modules\@deepseek-ai\dsh\package.json，
    /// 再退到 profile 自己的 node_modules。比发现服务全盘扫描快得多，且与安装插件用的是同一份包。
    /// 读不到返回 "unknown"（兼容性判定会按"未知视为兼容"处理）。
    /// </summary>
    public string GetLocalDshVersion()
    {
        var candidates = new[]
        {
            Path.Combine(_dshHome, "profiles", "node_modules", DshPackageName, "package.json"),
            Path.Combine(ProfileDirectory, "node_modules", DshPackageName, "package.json"),
        };
        foreach (var candidate in candidates)
        {
            var version = ReadPackageVersion(candidate);
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        return "unknown";
    }

    /// <summary>读取 profile 清单里的 dsh.profile.bundles 数组为集合。</summary>
    private static IEnumerable<string> ReadBundles(JsonElement root)
    {
        if (!root.TryGetProperty("dsh", out var dsh) || dsh.ValueKind != JsonValueKind.Object) yield break;
        if (!dsh.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object) yield break;
        if (!profile.TryGetProperty("bundles", out var bundles) || bundles.ValueKind != JsonValueKind.Array) yield break;

        foreach (var element in bundles.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) continue;
            var name = element.GetString();
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }

    /// <summary>读 &lt;profileDir&gt;\node_modules\&lt;name&gt;\package.json 的真实版本；读不到返回 null。</summary>
    private string? ResolveInstalledVersion(string packageName)
    {
        // Path.Combine 对作用域包名（@scope/name）会正确拼成 node_modules/@scope/name
        var manifestPath = Path.Combine(ProfileDirectory, "node_modules", packageName, "package.json");
        return ReadPackageVersion(manifestPath);
    }

    /// <summary>读取某个 package.json 的 version 字段；文件缺失 / 损坏返回 null。</summary>
    private static string? ReadPackageVersion(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>去掉版本规格的 ^ / ~ / = 前缀（仅展示用，不参与语义化计算）。</summary>
    private static string NormalizeVersionSpec(string spec)
    {
        var trimmed = spec.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed[0] is '^' or '~' or '=' or 'v' or '>')
            trimmed = trimmed[1..].TrimStart(' ', '=');
        return trimmed;
    }

    // ==================== 安装 / 卸载 ====================

    /// <summary>校验 npm 包名：允许 <c>name</c> 或 <c>@scope/name</c>，字母数字与 <c>._-~</c>。</summary>
    /// <remarks>
    /// 【为什么要校验】包名/版本来自 registry 返回的 JSON，最终被拼进 node 的 argv
    /// （<c>add &lt;name&gt;@&lt;version&gt;</c>）。pnpm 把空格后的 token 当独立选项解析，
    /// 所以形如 <c>foo --registry=http://evil</c> 的"包名"能合法地改写安装源，
    /// 让面板从攻击者仓库装包并执行其安装脚本。合法包名不可能含空格或引号，直接拒绝最稳。
    ///
    /// 规则本体已抽到 <see cref="PackageSpecValidator"/>，与插件页手输框的安装路径共用同一道防线，
    /// 避免出现「市场页挡住了、手输框还能注入」的缺口。
    /// </remarks>
    private static bool IsSafePackageName(string name) => PackageSpecValidator.IsSafePackageName(name);

    /// <summary>校验版本/标签字符串：只允许 semver 与常见 tag 字符，禁止空格与引号。</summary>
    private static bool IsSafeVersion(string version) => PackageSpecValidator.IsSafeVersion(version);

    /// <summary>
    /// 安装：node &lt;bin.js&gt; plugin --profile &lt;profile&gt; add &lt;包名&gt;@&lt;版本&gt;。
    /// 逐行回传输出（onOutput）；成功（退出码 0）返回 true，其余返回 false 并回传中文原因。
    /// </summary>
    public async Task<bool> InstallAsync(string packageName, string version, Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var name = packageName?.Trim() ?? string.Empty;
        if (name.Length == 0) return false;

        // 参数注入防线：包名/版本要经 node argv 传给 pnpm，非法字符能让它改解析成额外选项
        if (!IsSafePackageName(name))
        {
            onOutput?.Invoke($"❌ 包名不合法，已拒绝安装：{name}（包名只能包含字母、数字与 . _ - ~ /）");
            return false;
        }

        var trimmedVersion = version?.Trim() ?? string.Empty;
        if (trimmedVersion.Length > 0 && !IsSafeVersion(trimmedVersion))
        {
            onOutput?.Invoke($"❌ 版本号不合法，已拒绝安装：{trimmedVersion}（版本只能包含字母、数字与 . - + _ ~ ^ * >）");
            return false;
        }

        var spec = trimmedVersion.Length == 0 ? name : $"{name}@{trimmedVersion}";
        return await RunCliAsync($"add {spec}", onOutput, ct).ConfigureAwait(false);
    }

    /// <summary>卸载：node &lt;bin.js&gt; plugin --profile &lt;profile&gt; remove &lt;包名&gt;。</summary>
    public async Task<bool> UninstallAsync(string packageName, Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var name = packageName?.Trim() ?? string.Empty;
        if (name.Length == 0) return false;

        // 与安装同一条理由：包名是 argv 片段，空格后的 token 会被 pnpm 当独立选项
        if (!IsSafePackageName(name))
        {
            onOutput?.Invoke($"❌ 包名不合法，已拒绝卸载：{name}（包名只能包含字母、数字与 . _ - ~ /）");
            return false;
        }

        return await RunCliAsync($"remove {name}", onOutput, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 在子进程运行一条 dsh plugin 命令：node + bin.js 直跑（绕开坏 shim），
    /// 实时逐行回传输出、支持超时与取消；启动失败 / 超时 / 非零退出一律返回 false，不抛异常。
    /// </summary>
    private async Task<bool> RunCliAsync(string pluginArgs, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var binJs = DshBinJsPath;
        if (!File.Exists(binJs))
        {
            onOutput?.Invoke($"❌ 未找到 dsh 入口：{binJs}，无法执行插件命令");
            return false;
        }

        var arguments = $"\"{binJs}\" plugin --profile {_profileName} {pluginArgs}";
        onOutput?.Invoke($"▶ node {arguments}");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // 统一按 UTF-8 解码输出，避免中文乱码（pnpm 输出含中文提示）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };

        try
        {
            if (!process.Start())
            {
                onOutput?.Invoke("❌ 无法启动 node 进程");
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 叠加超时与调用方取消信号：任一先到即强制结束
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_commandTimeout);

            var exited = true;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                onOutput?.Invoke(cancellationToken.IsCancellationRequested
                    ? "⚠️ 插件命令已被取消"
                    : "⚠️ 插件命令执行超时（超过 5 分钟）");
                return false;
            }

            var ok = process.ExitCode == 0;
            if (!ok) onOutput?.Invoke($"❌ 插件命令退出码非 0：{process.ExitCode}");
            return ok;
        }
        catch (Exception ex)
        {
            // 启动失败（node 不在 PATH / 权限不足）：不崩溃，回传中文原因
            onOutput?.Invoke($"❌ 启动插件命令失败：{ex.Message}");
            return false;
        }
    }

    // ==================== 版本兼容性判断 ====================

    /// <summary>
    /// 判断某版本范围是否与本机 dsh 版本兼容。
    /// 支持 `&gt;=`、`&gt;`、`&lt;=`、`&lt;`、`^`、`~`、`=` 与空格分隔的与关系；
    /// 无法解析（OR 区间、通配、非法版本）时**返回 true**（未知视为兼容），避免把正常插件误标为不兼容。
    /// </summary>
    public static bool IsDshCompatible(string? requirementRange, string currentDshVersion)
    {
        if (string.IsNullOrWhiteSpace(requirementRange)) return true;   // 无版本要求 → 兼容
        if (!TryParseVersion(currentDshVersion, out var current)) return true;  // 本机版本不可知 → 不误报

        var range = requirementRange.Trim();
        // 本实现只覆盖简单范围：出现 OR、连字符区间、通配则放弃判定 → 视为兼容
        if (range.Contains("||") || range.Contains(" - ") || range.Contains('*')) return true;

        foreach (var token in range.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var verdict = EvaluateToken(token, current);
            if (verdict is null) return true;      // 单个条件无法解析 → 整体按未知处理
            if (!verdict.Value) return false;      // 与关系：任一条件不满足即不兼容
        }
        return true;
    }

    /// <summary>
    /// 比较两个语义化版本：负数 = left 更小，0 = 相等，正数 = left 更大。
    /// 任一无法解析时返回 0（相等）——调用方（IsDshCompatible）已先做解析检查，不会因此误判。
    /// </summary>
    public static int CompareVersions(string? left, string? right)
    {
        if (!TryParseVersion(left, out var a) || !TryParseVersion(right, out var b)) return 0;
        return CompareParsed(a, b);
    }

    /// <summary>解析单个条件（如 "&gt;=0.1.0-rc.5" / "^0.1.0"）；返回 null 表示无法解析。</summary>
    private static bool? EvaluateToken(string token, SemVer current)
    {
        if (token.Length == 0) return null;

        // 操作符最长优先匹配（">=" 必须先于 ">"）
        var op = string.Empty;
        var rest = token;
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "^", "~", "=" })
        {
            if (!token.StartsWith(candidate, StringComparison.Ordinal)) continue;
            op = candidate;
            rest = token[candidate.Length..].Trim();
            break;
        }

        if (rest.Length == 0) return null;
        if (!TryParseVersion(rest, out var bound)) return null;   // 含 x/* 等通配写法会落到这里

        var comparison = CompareParsed(current, bound);
        return op switch
        {
            ">=" => comparison >= 0,
            ">" => comparison > 0,
            "<=" => comparison <= 0,
            "<" => comparison < 0,
            // ^0.1.0 → [0.1.0, 0.2.0)；^1.2.3 → [1.2.3, 2.0.0)
            "^" => comparison >= 0 && CompareParsed(current, CaretUpperBound(bound)) < 0,
            // ~1.2.3 → [1.2.3, 1.3.0)；~1 → [1.0.0, 2.0.0)
            "~" => comparison >= 0 && CompareParsed(current, TildeUpperBound(bound)) < 0,
            // 无操作符或 "=" 按精确版本
            _ => comparison == 0,
        };
    }

    /// <summary>caret 上界：首个非零段进位（0.1.0 → 0.2.0；1.2.3 → 2.0.0；0.0.3 → 0.0.4）。</summary>
    private static SemVer CaretUpperBound(SemVer bound)
    {
        if (bound.Major > 0 || bound.Parts == 1) return new SemVer(bound.Major + 1, 0, 0, Array.Empty<string>(), 3);
        if (bound.Minor > 0 || bound.Parts == 2) return new SemVer(0, bound.Minor + 1, 0, Array.Empty<string>(), 3);
        return new SemVer(0, 0, bound.Patch + 1, Array.Empty<string>(), 3);
    }

    /// <summary>tilde 上界：只写主版本时进位主版本，否则进位次版本（~1 → 2.0.0；~1.2 → 1.3.0）。</summary>
    private static SemVer TildeUpperBound(SemVer bound)
        => bound.Parts <= 1
            ? new SemVer(bound.Major + 1, 0, 0, Array.Empty<string>(), 3)
            : new SemVer(bound.Major, bound.Minor + 1, 0, Array.Empty<string>(), 3);

    /// <summary>逐段比较：先比数字段，再比预发布标识（预发布 &lt; 同号正式版）。</summary>
    private static int CompareParsed(SemVer left, SemVer right)
    {
        var comparison = left.Major.CompareTo(right.Major);
        if (comparison != 0) return comparison;
        comparison = left.Minor.CompareTo(right.Minor);
        if (comparison != 0) return comparison;
        comparison = left.Patch.CompareTo(right.Patch);
        if (comparison != 0) return comparison;
        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    /// <summary>预发布标识比较（语义化版本规范第 11 条）。</summary>
    private static int ComparePrerelease(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;    // 无预发布 = 正式版 → 更大
        if (right.Length == 0) return -1;

        var count = Math.Min(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var isLeftNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var isRightNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            int comparison;
            if (isLeftNumeric && isRightNumeric) comparison = leftNumber.CompareTo(rightNumber);
            else if (isLeftNumeric) comparison = -1;    // 数字标识符优先级低于字母标识符
            else if (isRightNumeric) comparison = 1;
            else comparison = string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        // 前缀全部相同时，标识符更少的更小（rc < rc.1）
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>
    /// 解析语义化版本（容忍前置 v/= 与 build metadata；预发布保留在 - 之后）。
    /// </summary>
    private static bool TryParseVersion(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.Length > 0 && (value[0] is 'v' or 'V' or '=')) value = value[1..];
        if (value.Length == 0) return false;

        // build metadata（+ 之后）不参与比较
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        // 第一个 - 之后是预发布标识（如 0.1.5-rc.3 → "rc.3"）
        var prerelease = Array.Empty<string>();
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
            value = value[..dash];
        }

        var segments = value.Split('.');
        if (segments.Length is 0 or > 4) return false;

        var numbers = new int[3];
        for (var i = 0; i < segments.Length && i < 3; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;   // 含 x / * 等非数字段 → 无法解析
        }

        version = new SemVer(numbers[0], numbers[1], numbers[2], prerelease, Math.Min(segments.Length, 3));
        return true;
    }

    /// <summary>解析出的语义化版本；Parts 记录用户实际写了几个数字段（供 ^ / ~ 上界推导）。</summary>
    private readonly struct SemVer
    {
        public SemVer(int major, int minor, int patch, string[] prerelease, int parts)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease;
            Parts = parts;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string[] Prerelease { get; }
        public int Parts { get; }
    }

    // ==================== 客户端排序 / 分页（服务端排序不可用时的替代） ====================

    /// <summary>
    /// 客户端排序：按模式重排条目（不修改入参，返回新列表）。
    /// - UpdatedTime：发布时间降序，无发布时间的排最后（OrderByDescending(bool) 让"有日期"的先排）；
    /// - Downloads：下载量降序（LINQ 排序稳定，同分保持原服务端顺序）；
    /// - Relevance：原样返回（调用方通常直接走服务端分页）。
    /// </summary>
    public static List<PluginMarketItem> SortByMode(IEnumerable<PluginMarketItem> items, MarketSortMode mode)
    {
        var list = items?.Where(i => i is not null).ToList() ?? new List<PluginMarketItem>();
        return mode switch
        {
            MarketSortMode.UpdatedTime => list
                .OrderByDescending(i => i.PublishedAt.HasValue)          // 有日期的一律在前
                .ThenByDescending(i => i.PublishedAt ?? DateTime.MinValue)
                .ToList(),
            MarketSortMode.Downloads => list
                .OrderByDescending(i => i.Downloads)
                .ToList(),
            _ => list,
        };
    }

    /// <summary>
    /// 客户端分页：对已排序集合做 Skip/Take。
    /// page 为 0 基；pageSize 非法时按 1 处理；越界页返回空集合（界面据此把页码夹回末页）。
    /// </summary>
    public static IReadOnlyList<T> PageItems<T>(IReadOnlyList<T> source, int page, int pageSize)
    {
        if (source is null || source.Count == 0) return Array.Empty<T>();

        var size = Math.Max(1, pageSize);
        var start = Math.Max(0, page) * size;
        if (start >= source.Count) return Array.Empty<T>();

        return source.Skip(start).Take(size).ToList();
    }

    /// <summary>
    /// 兼容性过滤规则（只看兼容本机版本）：
    /// - 没有 dshhub 声明（HasDshHub=false）→ 保留：没有声明不等于不兼容，未知即不排除，避免误杀；
    /// - 有声明且判定为不兼容（HasDshHub=true &amp;&amp; IsCompatible=false）→ 过滤掉；
    /// - 有声明且兼容 → 保留。
    /// </summary>
    public static bool ShouldKeepByCompatibility(PluginMarketItem item, bool onlyCompatible)
        => item is not null && (!onlyCompatible || !item.HasDshHub || item.IsCompatible);

    /// <summary>应用兼容性过滤，返回可展示的条目（不修改入参）。</summary>
    public static List<PluginMarketItem> FilterByCompatibility(IEnumerable<PluginMarketItem> items, bool onlyCompatible)
        => (items ?? Enumerable.Empty<PluginMarketItem>())
            .Where(i => ShouldKeepByCompatibility(i, onlyCompatible))
            .ToList();

    // ==================== 版本更新判定 ====================

    /// <summary>
    /// 判断"是否有更新"：最新版本严格大于已装版本才算可更新。
    /// 任一版本无法解析（空 / "unknown" / 通配）返回 false —— 无法确定时不提示更新，避免误导用户点更新。
    /// 预发布号按语义化规范比较（0.1.5-rc.3 &lt; 0.1.5-rc.10），且预发布 &lt; 同号正式版。
    /// </summary>
    public static bool IsUpdateAvailable(string? installedVersion, string? latestVersion)
    {
        if (!TryParseVersion(installedVersion, out var installed)) return false;
        if (!TryParseVersion(latestVersion, out var latest)) return false;
        return CompareParsed(installed, latest) < 0;
    }

    /// <summary>
    /// 查询一批包的市场最新版本（GET {registry}/{name}/latest，限量并发 5）。
    /// 返回值：包名 → 版本号；查不到（私有包 / 已下架 / 网络失败）时对应值为 null，
    /// 由界面显示"市场未找到"且不视为错误。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetLatestVersionsAsync(
        IEnumerable<string> packageNames, CancellationToken ct = default, Action<int, int>? onProgress = null)
    {
        var names = (packageNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            onProgress?.Invoke(0, 0);
            return result;
        }

        using var gate = new SemaphoreSlim(MaxConcurrentDetailRequests);
        var total = names.Count;
        var done = 0;
        var sync = new object();

        async Task QueryOneAsync(string name)
        {
            var acquired = false;
            string? version = null;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                var detail = await GetDetailAsync(name, ct).ConfigureAwait(false);
                version = string.IsNullOrWhiteSpace(detail?.Version) ? null : detail!.Version;
            }
            catch
            {
                // 单包失败 → 记为未找到，不影响其它包
            }
            finally
            {
                if (acquired) gate.Release();
                var current = Interlocked.Increment(ref done);
                lock (sync) result[name] = version;
                onProgress?.Invoke(current, total);
            }
        }

        await Task.WhenAll(names.Select(QueryOneAsync)).ConfigureAwait(false);
        return result;
    }

    // ==================== 下载量（最近一周） ====================

    /// <summary>构造单批下载量请求 URL：{base}/downloads/point/last-week/&lt;包名用逗号连接&gt;。</summary>
    /// <remarks>
    /// 【为什么要逐个转义】包名来自 registry 返回的 JSON（外部数据）。若不转义直接拼进 URL 路径，
    /// 一个含 <c>/../</c> 或 <c>?</c> 的"包名"就能改写请求路径 / 注入查询参数，
    /// 把下载量请求引到攻击者指定的地址。作用域包 <c>@scope/name</c> 的 <c>@</c> 与 <c>/</c>
    /// 经 EscapeDataString 变成 %40 与 %2F —— 对 API 而言 %2F 就是路径里的斜杠，语义不变。
    /// 注意逗号是 API 规定的分隔符，因此先转义各元素、再用未转义的 "," 连接。
    /// </remarks>
    public static string BuildDownloadsUrl(string? downloadsBase, IEnumerable<string> packageNames)
    {
        var baseUrl = string.IsNullOrWhiteSpace(downloadsBase) ? DefaultDownloadsBase : downloadsBase.TrimEnd('/');
        var names = (packageNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Uri.EscapeDataString(n.Trim()));
        return $"{baseUrl}/downloads/point/last-week/{string.Join(",", names)}";
    }

    /// <summary>
    /// 把包名按 <paramref name="batchSize"/> 切分为多批（保持原顺序、去重）。
    /// 用于下载量接口：URL 过长必须分批，每批不超过 batchSize。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> SplitIntoBatches(
        IEnumerable<string> packageNames, int batchSize = DownloadsBatchSize)
    {
        var size = Math.Max(1, batchSize);
        var names = (packageNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var batches = new List<IReadOnlyList<string>>();
        for (var i = 0; i < names.Count; i += size)
            batches.Add(names.GetRange(i, Math.Min(size, names.Count - i)));
        return batches;
    }

    /// <summary>
    /// 批量获取最近一周下载量：分批（≤60）**串行**请求，经 onProgress 回调汇报进度（已完成包数 / 总包数）。
    /// 【失败降级】单批失败时该批包的下载量保持 0，不中断整体（排序仍可用，只是这些包排到后面）；
    /// 失败批次数通过 onWarning 回调上报，调用方据此提示"部分下载量缺失"，避免用户把 0 当成真实数据。
    /// 返回：包名 → 下载量；未查到的包为 0。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> FetchWeeklyDownloadsAsync(
        IEnumerable<string> packageNames, Action<int, int>? onProgress = null, CancellationToken ct = default,
        Action<int, int>? onWarning = null)
    {
        var names = (packageNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            onProgress?.Invoke(0, 0);
            return result;
        }

        // 预先全部置 0：失败批次的包也保留键，保证排序集合完整
        foreach (var name in names) result[name] = 0;

        var total = names.Count;
        var processed = 0;
        var failedBatches = 0;
        var failedPackages = 0;
        foreach (var batch in SplitIntoBatches(names, DownloadsBatchSize))
        {
            try
            {
                var json = await Http.GetStringAsync(BuildDownloadsUrl(DefaultDownloadsBase, batch), ct)
                    .ConfigureAwait(false);
                MergeDownloads(json, result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;  // 用户取消 → 停止后续批次，已获取的数据仍可用
            }
            catch
            {
                // 单批失败（网络 / 解析）→ 该批保持 0，继续下一批，不中断整体。
                // 【为什么要计数】旧实现这里完全静默：失败批次的包下载量停在 0，
                // 用户按"下载量"排序时看到的是"这些包没人下"，而真相是"这批数据没拉到"。
                // 计数后交给调用方在状态栏说明，界面不再把缺失伪装成真实排名。
                failedBatches++;
                failedPackages += batch.Count;
            }

            processed += batch.Count;
            onProgress?.Invoke(processed, total);
        }

        if (failedBatches > 0)
            onWarning?.Invoke(failedBatches, failedPackages);

        return result;
    }

    /// <summary>解析下载量响应（{ "包名": { downloads: N, ... }, ... }）并写入结果表。</summary>
    private static void MergeDownloads(string json, IDictionary<string, int> result)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (property.Value.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt32(out var count))
                result[property.Name] = count;
        }
    }

    // ==================== 小工具 ====================

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DshManager/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
        }
        return list;
    }

    /// <summary>读两层嵌套的字符串数组，如 dshhub.capabilities.provides / dshhub.permissions.network。</summary>
    private static IReadOnlyList<string> ReadNestedStringArray(JsonElement element, string parentName, string childName)
    {
        if (!element.TryGetProperty(parentName, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        return ReadStringArray(parent, childName);
    }

    /// <summary>repository 既可能是字符串，也可能是 { type, url } 对象，两种都取 URL。</summary>
    private static string ReadRepositoryUrl(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repository)) return string.Empty;
        return repository.ValueKind switch
        {
            JsonValueKind.String => repository.GetString() ?? string.Empty,
            JsonValueKind.Object => ReadString(repository, "url"),
            _ => string.Empty,
        };
    }
}
