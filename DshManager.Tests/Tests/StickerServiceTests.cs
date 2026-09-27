using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// StickerService 单元测试。
/// 全部使用临时目录（构造时注入内置/用户两个目录），绝不触碰真实的 %LOCALAPPDATA%，
/// 每个测试方法独立实例、结束后递归清理临时根目录。
/// </summary>
public class StickerServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _builtIn;
    private readonly string _user;

    public StickerServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
        _builtIn = Path.Combine(_root, "builtin");
        _user = Path.Combine(_root, "user");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>构造被测服务（注入临时目录，避免污染真实用户目录）。</summary>
    private StickerService CreateService() => new(_builtIn, _user);

    /// <summary>在指定目录写入一个占位文件并返回其绝对路径。</summary>
    private static string WriteFile(string directory, string fileName, string content = "sticker")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ==================== 构造 & 目录创建 ====================

    /// <summary>构造时应自动创建用户目录，但不主动创建内置目录。</summary>
    [Fact]
    public void Constructor_CreatesUserDirectoryOnly()
    {
        Assert.False(Directory.Exists(_user));

        _ = CreateService();

        Assert.True(Directory.Exists(_user));
        Assert.False(Directory.Exists(_builtIn));
    }

    // ==================== ListAll ====================

    /// <summary>用户目录优先 + 内置目录补充，同名去重（用户胜出），并按文件名排序。</summary>
    [Fact]
    public void ListAll_UserWinsOnDuplicate_AndSortsByFileName()
    {
        WriteFile(_builtIn, "a.png");
        WriteFile(_builtIn, "shared.png", "builtin");
        WriteFile(_builtIn, "z.png");
        WriteFile(_user, "shared.png", "user");
        WriteFile(_user, "b.png");

        var list = CreateService().ListAll();

        // 共 4 个（shared 去重），按文件名排序
        Assert.Equal(
            new[] { "a.png", "b.png", "shared.png", "z.png" },
            list.Select(path => Path.GetFileName(path)));

        // 同名时保留用户目录的版本
        var shared = Assert.Single(list.Where(p => Path.GetFileName(p) == "shared.png"));
        Assert.Equal(Path.Combine(_user, "shared.png"), shared);
    }

    /// <summary>非图片扩展名不进入列表。</summary>
    [Fact]
    public void ListAll_IgnoresUnsupportedExtensions()
    {
        WriteFile(_builtIn, "a.png");
        WriteFile(_builtIn, "readme.txt");
        WriteFile(_user, "b.jpg");

        var list = CreateService().ListAll();

        Assert.Equal(new[] { "a.png", "b.jpg" }, list.Select(path => Path.GetFileName(path)));
    }

    /// <summary>目录不存在时返回空列表且不抛异常。</summary>
    [Fact]
    public void ListAll_WhenDirectoriesMissing_ReturnsEmpty()
    {
        var list = CreateService().ListAll();

        Assert.Empty(list);
    }

    // ==================== AddSticker ====================

    /// <summary>添加成功：文件复制进用户目录并返回新路径，内容一致。</summary>
    [Fact]
    public void AddSticker_CopiesFileAndReturnsNewPath()
    {
        var source = WriteFile(Path.Combine(_root, "incoming"), "cat.png", "meow");

        var result = CreateService().AddSticker(source, out var message);

        Assert.NotNull(result);
        Assert.True(File.Exists(result));
        Assert.Equal("cat.png", Path.GetFileName(result!));
        Assert.Equal(_user, Path.GetDirectoryName(result!));
        Assert.Equal("meow", File.ReadAllText(result!));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    /// <summary>同名文件已存在时不覆盖，自动改名为 name-2、name-3 …。</summary>
    [Fact]
    public void AddSticker_DuplicateName_AppendsSequenceNumber()
    {
        var source = WriteFile(Path.Combine(_root, "incoming"), "cat.png", "new");
        var existing = WriteFile(_user, "cat.png", "old");
        var service = CreateService();

        var second = service.AddSticker(source, out _);
        var third = service.AddSticker(source, out _);

        Assert.Equal("cat-2.png", Path.GetFileName(second!));
        Assert.Equal("cat-3.png", Path.GetFileName(third!));
        // 原文件内容保持不动，未被覆盖
        Assert.Equal("old", File.ReadAllText(existing));
    }

    /// <summary>用户目录被外部删除后，添加操作应自动重建目录并成功。</summary>
    [Fact]
    public void AddSticker_RecreatesMissingUserDirectory()
    {
        var source = WriteFile(Path.Combine(_root, "incoming"), "cat.png");
        var service = CreateService();
        Directory.Delete(_user, recursive: true);

        var result = service.AddSticker(source, out _);

        Assert.True(Directory.Exists(_user));
        Assert.NotNull(result);
        Assert.True(File.Exists(result));
    }

    /// <summary>非法扩展名被拒绝，返回 null 且给出中文原因。</summary>
    [Fact]
    public void AddSticker_RejectsUnsupportedExtension()
    {
        var source = WriteFile(Path.Combine(_root, "incoming"), "note.txt");

        var result = CreateService().AddSticker(source, out var message);

        Assert.Null(result);
        Assert.Contains("不支持", message);
        // 被拒绝的文件不应进入用户目录
        Assert.Empty(Directory.GetFiles(_user));
    }

    /// <summary>源文件不存在时返回 null 并给出中文原因。</summary>
    [Fact]
    public void AddSticker_WhenSourceMissing_ReturnsNull()
    {
        var missing = Path.Combine(_root, "incoming", "nope.png");

        var result = CreateService().AddSticker(missing, out var message);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ==================== DeleteSticker ====================

    /// <summary>删除用户目录下的表情成功，文件被移除。</summary>
    [Fact]
    public void DeleteSticker_UserFile_RemovesFile()
    {
        var path = WriteFile(_user, "a.png");
        var service = CreateService();

        var ok = service.DeleteSticker(path, out var message);

        Assert.True(ok);
        Assert.False(File.Exists(path));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    /// <summary>内置目录的表情拒绝删除，文件保留并给出中文原因。</summary>
    [Fact]
    public void DeleteSticker_BuiltInFile_Rejected()
    {
        var path = WriteFile(_builtIn, "a.png");
        var service = CreateService();

        var ok = service.DeleteSticker(path, out var message);

        Assert.False(ok);
        Assert.True(File.Exists(path));
        Assert.Contains("内置表情不可删除", message);
    }

    /// <summary>用户目录与内置目录之外的文件拒绝删除。</summary>
    [Fact]
    public void DeleteSticker_FileOutsideStickerDirectories_Rejected()
    {
        var path = WriteFile(Path.Combine(_root, "other"), "a.png");
        var service = CreateService();

        var ok = service.DeleteSticker(path, out var message);

        Assert.False(ok);
        Assert.True(File.Exists(path));
        Assert.Contains("只能删除", message);
    }

    /// <summary>文件不存在时返回 false 并给出中文原因。</summary>
    [Fact]
    public void DeleteSticker_MissingFile_ReturnsFalse()
    {
        var missing = Path.Combine(_user, "nope.png");

        var ok = CreateService().DeleteSticker(missing, out var message);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}
