using DshManager.Models;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// JsonStoreService 单元测试。
/// 每个测试方法使用独立的临时数据目录（xunit 每个测试方法新建实例），
/// 避免污染项目内的 Data/ 目录，测试结束后自动清理。
/// </summary>
public class JsonStoreServiceTests : IDisposable
{
    private readonly string _tempDataDir;

    public JsonStoreServiceTests()
    {
        // 为每个测试创建独立的临时目录
        _tempDataDir = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        // 测试结束后清理临时目录
        if (Directory.Exists(_tempDataDir))
        {
            Directory.Delete(_tempDataDir, recursive: true);
        }
    }

    /// <summary>写入后回读一致：保存 AppSettings 后读取，各字段应与写入值相等。</summary>
    [Fact]
    public void SaveThenLoad_ReturnsSameValues()
    {
        var store = new JsonStoreService(_tempDataDir);
        var expected = new AppSettings
        {
            WallpaperPath = @"C:\wallpapers\ocean.jpg",
            WallpaperBlur = 0.35,
            WallpaperOpacity = 0.72,
            IsWallpaperEnabled = true,
        };

        store.Save("settings.json", expected);

        var actual = store.Load<AppSettings>("settings.json");
        Assert.NotNull(actual);
        Assert.Equal(expected.WallpaperPath, actual!.WallpaperPath);
        Assert.Equal(expected.WallpaperBlur, actual.WallpaperBlur);
        Assert.Equal(expected.WallpaperOpacity, actual.WallpaperOpacity);
        Assert.Equal(expected.IsWallpaperEnabled, actual.IsWallpaperEnabled);
    }

    /// <summary>文件不存在时返回默认值（new T()），且不抛异常。</summary>
    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaultValue()
    {
        var store = new JsonStoreService(_tempDataDir);

        // 目录尚未创建、文件不存在，应返回默认值而不是抛异常
        var result = store.Load<AppSettings>("settings.json");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result!.WallpaperPath);
        Assert.False(result.IsWallpaperEnabled);
    }

    /// <summary>文件损坏（非法 JSON）时回退默认值，且不抛异常。</summary>
    [Fact]
    public void Load_WhenFileCorrupted_ReturnsDefaultValue()
    {
        // 先写入一个非法的 JSON 文本模拟文件损坏
        Directory.CreateDirectory(_tempDataDir);
        File.WriteAllText(Path.Combine(_tempDataDir, "settings.json"), "{ this is not valid json !!!");

        var store = new JsonStoreService(_tempDataDir);
        var result = store.Load<AppSettings>("settings.json");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result!.WallpaperPath);
    }

    /// <summary>保存时应自动创建数据目录，且生成的文件可被反序列化。</summary>
    [Fact]
    public void Save_CreatesDataDirectoryAutomatically()
    {
        var store = new JsonStoreService(_tempDataDir);

        store.Save("personas.json", new PersonaTemplate
        {
            Name = "助手",
            SystemPrompt = "你是一个乐于助人的助手。",
            Description = "通用助手人格",
            CreatedAt = DateTime.UtcNow,
        });

        // 目录与文件应已自动创建
        Assert.True(Directory.Exists(_tempDataDir));
        Assert.True(File.Exists(Path.Combine(_tempDataDir, "personas.json")));
    }

    /// <summary>列表类型（如记忆条目）也能正常写入与回读。</summary>
    [Fact]
    public void SaveAndLoad_ListType_Works()
    {
        var store = new JsonStoreService(_tempDataDir);
        var entries = new List<MemoryEntry>
        {
            new()
            {
                AgentOrProject = "DshManager",
                Content = "已完成 JSON 存储层。",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
        };

        store.Save("memory.json", entries);

        var actual = store.Load<List<MemoryEntry>>("memory.json");
        Assert.NotNull(actual);
        var entry = Assert.Single(actual!);
        Assert.Equal("DshManager", entry.AgentOrProject);
        Assert.Equal("已完成 JSON 存储层。", entry.Content);
    }
}
