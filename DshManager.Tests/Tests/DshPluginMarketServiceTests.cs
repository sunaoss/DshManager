using System.IO;
using System.Text;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// DshPluginMarketService 单元测试。
/// 全部离线：版本比较 / dshhub 解析 / URL 拼装都是纯函数，已安装清单解析用临时目录伪造 profile，
/// 绝不发起真实网络请求，也绝不触碰真实的 ~/.dsh。
/// </summary>
public class DshPluginMarketServiceTests : IDisposable
{
    private const string ProfileName = "web";

    /// <summary>本测试独占的临时 DSH_HOME。</summary>
    private readonly string _base;

    public DshPluginMarketServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "DshManagerMarketTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private DshPluginMarketService NewService()
        => new(registryBase: "https://registry.npmjs.org", dshHome: _base, profileName: ProfileName);

    private string ProfileDir => Path.Combine(_base, "profiles", ProfileName);

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    // ==================== IsDshCompatible ====================

    /// <summary>dshhub.compatibility.dsh 的各种范围与 0.1.5-rc.3 的兼容判定。</summary>
    [Theory]
    [InlineData(">=0.1.0-rc.5", true)]                 // 预发布号参与比较：0.1.5-rc.3 > 0.1.0-rc.5
    [InlineData(">=0.1.6", false)]                     // 0.1.5 < 0.1.6
    [InlineData("^0.1.0", true)]                       // [0.1.0, 0.2.0)
    [InlineData("~0.1", true)]                         // [0.1.0, 0.2.0)
    [InlineData(">=0.1.0 <0.2.0", true)]               // 空格分隔 = 与
    [InlineData(">=0.1.0 <0.1.4", false)]              // 上界不满足
    [InlineData(">=1.0.0", false)]
    [InlineData(">0.1.5-rc.3", false)]                 // 严格大于自身不成立
    [InlineData("<=0.1.5-rc.3", true)]
    [InlineData("=0.1.5-rc.3", true)]
    [InlineData("^0.0.3", false)]                      // ^0.0.3 → [0.0.3, 0.0.4)
    [InlineData("^0.2.0", false)]
    [InlineData("<0.1.0", false)]
    public void IsDshCompatible_EvaluatesRange(string requirement, bool expected)
        => Assert.Equal(expected, DshPluginMarketService.IsDshCompatible(requirement, "0.1.5-rc.3"));

    /// <summary>无法解析 / 缺省 / 复杂范围 → 未知视为兼容，绝不能误报不兼容。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData(">=")]
    [InlineData("~abc")]
    [InlineData("1.x")]
    [InlineData("*")]
    [InlineData(">=0.1.0 || >=1.0.0")]
    [InlineData(">=0.1.0 - 0.2.0")]
    public void IsDshCompatible_UnknownRange_ReturnsTrue(string? requirement)
        => Assert.True(DshPluginMarketService.IsDshCompatible(requirement, "0.1.5-rc.3"));

    /// <summary>本机版本读不到（unknown）时不误报不兼容。</summary>
    [Fact]
    public void IsDshCompatible_UnknownCurrentVersion_ReturnsTrue()
        => Assert.True(DshPluginMarketService.IsDshCompatible(">=9.9.9", "unknown"));

    // ==================== CompareVersions 边界 ====================

    /// <summary>语义化版本比较：预发布小于同号正式版；数字标识符按数值比较。</summary>
    [Theory]
    [InlineData("0.1.5-rc.3", "0.1.5", -1)]      // 预发布 < 正式
    [InlineData("0.1.5", "0.1.5-rc.3", 1)]
    [InlineData("0.1.5-rc.3", "0.1.5-rc.10", -1)] // 3 < 10（数值比较，非字典序）
    [InlineData("0.1.5-rc.10", "0.1.5-rc.3", 1)]
    [InlineData("0.10.0", "0.9.0", 1)]            // 10 > 9
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]            // 容忍 v 前缀
    [InlineData("1.2.3+build.7", "1.2.3", 0)]     // build metadata 不参与比较
    [InlineData("1.0.0-rc.1", "1.0.0-beta.1", 1)] // rc > beta（字典序）
    [InlineData("0.1.5-rc", "0.1.5-rc.1", -1)]    // 前缀相同，标识符更少更小
    public void CompareVersions_ComparesSemantically(string left, string right, int expected)
        => Assert.Equal(expected, Math.Sign(DshPluginMarketService.CompareVersions(left, right)));

    // ==================== dshhub 解析 ====================

    /// <summary>实测样例（dsh-cost-meter 的 package.json）→ dshhub 各字段解析正确。</summary>
    private const string CostMeterJson = """
    {
      "name": "dsh-cost-meter",
      "version": "1.7.39",
      "description": "DeepSeek Harness session cost meter",
      "repository": { "type": "git", "url": "https://github.com/example/dsh-cost-meter" },
      "homepage": "https://example.com/cost-meter",
      "keywords": ["dsh-plugin", "cost"],
      "dshhub": {
        "schemaVersion": 1,
        "displayName": "dsh-cost-meter",
        "summary": "DeepSeek Harness 会话费用统计:本会话费用、预算图框、官方余额、...",
        "categories": ["费用", "Web UI", "观测"],
        "surfaces": ["host", "web"],
        "capabilities": { "provides": ["service:cost-meter"] },
        "compatibility": { "dsh": ">=0.1.0-rc.5", "node": ">=20" },
        "permissions": { "network": ["https://api.deepseek.com", "https://example.com/pricing"] }
      }
    }
    """;

    [Fact]
    public void TryParseDetail_ParsesDshHubMetadata()
    {
        Assert.True(DshPluginMarketService.TryParseDetail(CostMeterJson, "dsh-cost-meter", out var item));

        Assert.True(item.HasDshHub);
        Assert.Equal("dsh-cost-meter", item.Name);
        Assert.Equal("1.7.39", item.Version);
        Assert.Equal("dsh-cost-meter", item.Title);
        Assert.Contains("DeepSeek Harness", item.Summary);
        Assert.Equal(item.Summary, item.SummaryText);          // 有中文简介时优先用它
        Assert.Equal(3, item.Categories.Count);
        Assert.Contains("费用", item.Categories);
        Assert.Equal("费用 · Web UI · 观测", item.CategoriesText);
        Assert.Equal(2, item.Surfaces.Count);
        Assert.Contains("service:cost-meter", item.Provides);
        Assert.Equal(2, item.NetworkPermissions.Count);
        Assert.Contains("https://api.deepseek.com", item.NetworkPermissions);
        Assert.Equal(">=0.1.0-rc.5", item.DshRequirement);
        Assert.Equal(">=20", item.NodeRequirement);
        Assert.Equal("要求 dsh >=0.1.0-rc.5", item.CompatibilityText);
        Assert.Equal("https://github.com/example/dsh-cost-meter", item.RepositoryUrl);
        Assert.Equal("https://example.com/cost-meter", item.HomepageUrl);
        Assert.Equal("https://www.npmjs.com/package/dsh-cost-meter", item.NpmUrl);
        Assert.Contains("dsh-plugin", item.Keywords);
        Assert.True(item.IsCompatible);                        // 未判定前默认兼容
    }

    /// <summary>无 dshhub 段：HasDshHub=false，中文简介回退 npm description。</summary>
    [Fact]
    public void TryParseDetail_WithoutDshHub_FallsBackToDescription()
    {
        const string json = """
        { "name": "dsh-plain", "version": "0.0.1", "description": "一个没有 dshhub 的包" }
        """;

        Assert.True(DshPluginMarketService.TryParseDetail(json, "dsh-plain", out var item));

        Assert.False(item.HasDshHub);
        Assert.Empty(item.Categories);
        Assert.Empty(item.NetworkPermissions);
        Assert.Equal(string.Empty, item.DshRequirement);
        Assert.Equal(string.Empty, item.CompatibilityText);
        Assert.Equal("一个没有 dshhub 的包", item.SummaryText);   // Summary 空 → 回退 Description
        Assert.Equal("dsh-plain", item.Title);                   // DisplayName 空 → 回退包名
    }

    /// <summary>非法 JSON / 空文本：返回 false 且不抛异常。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void TryParseDetail_InvalidJson_ReturnsFalse(string json)
        => Assert.False(DshPluginMarketService.TryParseDetail(json, "x", out _));

    // ==================== BuildSearchUrl ====================

    /// <summary>空关键词 → keywords:dsh-plugin；from = page * size。</summary>
    [Fact]
    public void BuildSearchUrl_EmptyQuery_UsesKeywordFilter()
    {
        var url = DshPluginMarketService.BuildSearchUrl("https://registry.npmjs.org", null, 0, 20);

        Assert.Contains("keywords:dsh-plugin", url);
        Assert.Contains("size=20", url);
        Assert.Contains("from=0", url);
        Assert.StartsWith("https://registry.npmjs.org/-/v1/search?", url);
    }

    /// <summary>非空关键词 → 转义后与关键词做与（+）；分页 from 正确。</summary>
    [Fact]
    public void BuildSearchUrl_WithQuery_EncodesQueryAndComputesOffset()
    {
        var url = DshPluginMarketService.BuildSearchUrl("https://registry.npmjs.org/", "cost meter", 2, 25);

        Assert.Contains("cost%20meter+keywords:dsh-plugin", url);
        Assert.Contains("size=25", url);
        Assert.Contains("from=50", url);
        Assert.StartsWith("https://registry.npmjs.org/-/v1/search?", url);   // 尾部斜杠已归一
    }

    /// <summary>非法分页参数被夹紧，且实例方法使用构造时传入的 registry 源。</summary>
    [Fact]
    public void BuildSearchUrl_ClampsPagingAndUsesInstanceRegistry()
    {
        var service = new DshPluginMarketService(registryBase: "https://mirror.example.com/");

        var url = service.BuildSearchUrl("dsh", -3, 9999);

        Assert.Contains("https://mirror.example.com/-/v1/search?", url);
        Assert.Contains("size=250", url);
        Assert.Contains("from=0", url);
    }

    // ==================== GetInstalledPlugins ====================

    /// <summary>dependencies（含真实版本 / 安装规格）+ bundles → 已安装清单。</summary>
    [Fact]
    public void GetInstalledPlugins_ParsesDependenciesAndBundles()
    {
        WriteFile(Path.Combine(ProfileDir, "package.json"), """
        {
          "name": "profile-web",
          "dependencies": {
            "dsh-cost-meter": "^1.7.0",
            "dsh-plain": "2.0.0",
            "dsh-prefixed": "^3.1.4"
          },
          "dsh": { "profile": { "bundles": ["dsh-cost-meter", "dsh-extra"] } }
        }
        """);
        // node_modules 里有真实包 → 用真实版本覆盖安装规格
        WriteFile(Path.Combine(ProfileDir, "node_modules", "dsh-cost-meter", "package.json"),
            """{ "name": "dsh-cost-meter", "version": "1.7.39" }""");

        var installed = NewService().GetInstalledPlugins();

        Assert.Equal(4, installed.Count);
        Assert.Equal("1.7.39", installed["dsh-cost-meter"]);   // 真实版本优先
        Assert.Equal("2.0.0", installed["dsh-plain"]);
        Assert.Equal("3.1.4", installed["dsh-prefixed"]);     // 去掉 ^ 前缀
        Assert.Equal(string.Empty, installed["dsh-extra"]);    // 只在 bundles 里、无包目录 → 版本空
    }

    /// <summary>profile 清单缺失 → 空字典，不抛异常。</summary>
    [Fact]
    public void GetInstalledPlugins_MissingManifest_ReturnsEmpty()
        => Assert.Empty(NewService().GetInstalledPlugins());

    /// <summary>profile 清单损坏 → 空字典，不抛异常。</summary>
    [Fact]
    public void GetInstalledPlugins_CorruptedManifest_ReturnsEmptyWithoutThrowing()
    {
        WriteFile(Path.Combine(ProfileDir, "package.json"), "{ 这不是 JSON");

        Assert.Empty(NewService().GetInstalledPlugins());
    }

    /// <summary>本机 dsh 版本读 profiles\node_modules\@deepseek-ai\dsh\package.json。</summary>
    [Fact]
    public void GetLocalDshVersion_ReadsDshPackageManifest()
    {
        WriteFile(Path.Combine(_base, "profiles", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            """{ "name": "@deepseek-ai/dsh", "version": "0.1.5-rc.3" }""");

        Assert.Equal("0.1.5-rc.3", NewService().GetLocalDshVersion());
    }

    /// <summary>dsh 包清单缺失 → 返回 unknown（兼容性判定随后按未知处理）。</summary>
    [Fact]
    public void GetLocalDshVersion_WhenMissing_ReturnsUnknown()
        => Assert.Equal("unknown", NewService().GetLocalDshVersion());

    // ==================== 默认构造 ====================

    /// <summary>未指定 registry 时用官方源（镜像源的 search 接口不可用）。</summary>
    [Fact]
    public void Constructor_DefaultRegistry_IsNpmjs()
    {
        var service = new DshPluginMarketService();

        Assert.Equal(DshPluginMarketService.DefaultRegistryBase, service.RegistryBase);
        Assert.Equal("https://registry.npmjs.org", service.RegistryBase);
    }

    /// <summary>dshhub 字段可被市场服务写回条目（详情补全路径）。</summary>
    [Fact]
    public void PluginMarketItem_SetCompatibility_IsWritableByService()
    {
        var item = new PluginMarketItem { DshRequirement = ">=9.0.0" };
        Assert.True(item.IsCompatible);

        item.SetCompatibility(false);

        Assert.False(item.IsCompatible);
        Assert.Equal("要求 dsh >=9.0.0", item.CompatibilityText);
    }

    // ==================== 下载量批量 URL / 分批 ====================

    /// <summary>下载量 URL：固定域名 + last-week 路径 + 逗号连接包名。</summary>
    [Fact]
    public void BuildDownloadsUrl_JoinsPackageNamesWithComma()
    {
        var url = DshPluginMarketService.BuildDownloadsUrl(
            DshPluginMarketService.DefaultDownloadsBase, new[] { "dsh-better-sidebar", "dsh-cost-meter" });

        Assert.Equal("https://api.npmjs.org/downloads/point/last-week/dsh-better-sidebar,dsh-cost-meter", url);
    }

    /// <summary>下载量 URL：base 尾部斜杠归一、忽略空白包名。</summary>
    [Fact]
    public void BuildDownloadsUrl_NormalizesBaseAndSkipsBlank()
    {
        var url = DshPluginMarketService.BuildDownloadsUrl("https://mirror.example.com/", new[] { " a ", "", "b" });

        Assert.Equal("https://mirror.example.com/downloads/point/last-week/a,b", url);
    }

    /// <summary>分批：250 个包按 60 切成 60/60/60/60/10，每批不超过 60，无丢失且顺序不变。</summary>
    [Fact]
    public void SplitIntoBatches_ChunksBySizeUnderLimit()
    {
        var names = Enumerable.Range(0, 250).Select(i => $"pkg-{i}").ToList();

        var batches = DshPluginMarketService.SplitIntoBatches(names);

        Assert.Equal(5, batches.Count);
        Assert.Equal(new[] { 60, 60, 60, 60, 10 }, batches.Select(b => b.Count).ToArray());
        Assert.All(batches, b => Assert.True(b.Count <= DshPluginMarketService.DownloadsBatchSize));
        Assert.Equal(250, batches.Sum(b => b.Count));
        Assert.Equal("pkg-249", batches[^1][^1]);
    }

    /// <summary>分批：空输入 / 不足一批 / 去重（忽略大小写）。</summary>
    [Fact]
    public void SplitIntoBatches_HandlesSmallAndDuplicateInput()
    {
        Assert.Empty(DshPluginMarketService.SplitIntoBatches(Array.Empty<string>()));
        Assert.Single(DshPluginMarketService.SplitIntoBatches(new[] { "a", "b" }));

        // {"a","A","b"} 去重后剩 2 个 → batchSize=2 时正好 1 批
        var batches = DshPluginMarketService.SplitIntoBatches(new[] { "a", "A", "b" }, batchSize: 2);
        Assert.Single(batches);
        Assert.Equal(new[] { "a", "b" }, batches[0]);
    }

    // ==================== 客户端排序 ====================

    /// <summary>构造排序 / 分页用条目的小工具。</summary>
    private static PluginMarketItem Item(string name, DateTime? published, int downloads = 0)
        => new() { Name = name, PublishedAt = published, Downloads = downloads };

    /// <summary>按发布时间降序，无发布时间的排最后。</summary>
    [Fact]
    public void SortByMode_UpdatedTime_DescendingWithNullsLast()
    {
        var items = new[]
        {
            Item("old", new DateTime(2024, 1, 1)),
            Item("none", null),
            Item("new", new DateTime(2026, 9, 26)),
            Item("mid", new DateTime(2025, 6, 1)),
        };

        var sorted = DshPluginMarketService.SortByMode(items, MarketSortMode.UpdatedTime);

        Assert.Equal(new[] { "new", "mid", "old", "none" }, sorted.Select(i => i.Name).ToArray());
    }

    /// <summary>按下载量降序。</summary>
    [Fact]
    public void SortByMode_Downloads_Descending()
    {
        var items = new[]
        {
            Item("b", null, 44304),
            Item("a", null, 47446),
            Item("c", null, 0),
        };

        var sorted = DshPluginMarketService.SortByMode(items, MarketSortMode.Downloads);

        Assert.Equal(new[] { "a", "b", "c" }, sorted.Select(i => i.Name).ToArray());
    }

    /// <summary>相关度：保持原顺序不变。</summary>
    [Fact]
    public void SortByMode_Relevance_KeepsOriginalOrder()
    {
        var items = new[] { Item("z", null, 1), Item("a", null, 999) };

        var sorted = DshPluginMarketService.SortByMode(items, MarketSortMode.Relevance);

        Assert.Equal(new[] { "z", "a" }, sorted.Select(i => i.Name).ToArray());
    }

    // ==================== 客户端分页 ====================

    /// <summary>客户端分页边界：第 1 页 / 末页 / 越界页 / 空源 / 非法 pageSize。</summary>
    [Fact]
    public void PageItems_HandlesBoundaries()
    {
        var source = Enumerable.Range(0, 45).ToList();

        var firstPage = DshPluginMarketService.PageItems(source, 0, 20);
        Assert.Equal(20, firstPage.Count);
        Assert.Equal(0, firstPage[0]);
        Assert.Equal(19, firstPage[firstPage.Count - 1]);

        var lastPage = DshPluginMarketService.PageItems(source, 2, 20);
        Assert.Equal(5, lastPage.Count);
        Assert.Equal(40, lastPage[0]);
        Assert.Equal(44, lastPage[lastPage.Count - 1]);

        Assert.Empty(DshPluginMarketService.PageItems(source, 3, 20));                 // 越界页
        Assert.Empty(DshPluginMarketService.PageItems(Array.Empty<int>(), 0, 20));     // 空源
        Assert.Single(DshPluginMarketService.PageItems(source, 0, 0));                 // pageSize 非法 → 按 1
    }

    // ==================== 兼容过滤 ====================

    /// <summary>过滤规则：无 dshhub 声明保留（未知不排除）；有声明且不兼容过滤掉；兼容保留。</summary>
    [Fact]
    public void FilterByCompatibility_FollowsRules()
    {
        var noHub = new PluginMarketItem { Name = "no-hub" };                     // HasDshHub=false → 保留
        var compatible = new PluginMarketItem { Name = "ok", HasDshHub = true };  // 默认兼容 → 保留
        var incompatible = new PluginMarketItem { Name = "bad", HasDshHub = true };
        incompatible.SetCompatibility(false);

        var kept = DshPluginMarketService.FilterByCompatibility(
            new[] { noHub, compatible, incompatible }, onlyCompatible: true);

        Assert.Equal(new[] { "no-hub", "ok" }, kept.Select(i => i.Name).ToArray());
    }

    /// <summary>未开启过滤时原样保留（含不兼容项）。</summary>
    [Fact]
    public void FilterByCompatibility_DisabledKeepsEverything()
    {
        var incompatible = new PluginMarketItem { Name = "bad", HasDshHub = true };
        incompatible.SetCompatibility(false);

        var kept = DshPluginMarketService.FilterByCompatibility(new[] { incompatible }, onlyCompatible: false);

        Assert.Single(kept);
        Assert.False(DshPluginMarketService.ShouldKeepByCompatibility(incompatible, onlyCompatible: true));
        Assert.True(DshPluginMarketService.ShouldKeepByCompatibility(incompatible, onlyCompatible: false));
    }

    // ==================== 可更新判定 ====================

    /// <summary>已装 &lt; 最新才可更新；相等 / 本地更新 / 未知版本一律不提示。</summary>
    [Theory]
    [InlineData("1.7.37", "1.7.39", true)]
    [InlineData("1.7.39", "1.7.39", false)]              // 已是最新
    [InlineData("1.7.40", "1.7.37", false)]              // 本地比市场新 → 不提示
    [InlineData("0.1.5-rc.3", "0.1.5-rc.10", true)]      // 预发布号按数值比较（3 < 10）
    [InlineData("0.1.5-rc.10", "0.1.5-rc.3", false)]
    [InlineData("0.1.5-rc.3", "0.1.5", true)]            // 预发布 < 同号正式版
    [InlineData("", "1.0.0", false)]                     // 已装版本未知 → 不提示
    [InlineData("1.0.0", "", false)]                     // 市场版本未知 → 不提示
    [InlineData("unknown", "1.0.0", false)]
    [InlineData("1.0.0", "unknown", false)]
    public void IsUpdateAvailable_ComparesSemantically(string installed, string latest, bool expected)
        => Assert.Equal(expected, DshPluginMarketService.IsUpdateAvailable(installed, latest));
}
