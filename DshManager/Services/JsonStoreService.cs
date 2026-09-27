using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshManager.Services;

/// <summary>
/// JSON 文件存储服务：负责在 Data 目录下以 JSON 文件持久化各类数据。
/// 文件不存在时返回默认值，文件损坏（非法 JSON）时回退默认值，保证不崩溃。
/// </summary>
public class JsonStoreService
{
    private readonly string _dataDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>写入互斥锁：本实例的所有 Save 串行化，避免"读-改-写"互相覆盖。</summary>
    private readonly object _saveLock = new();

    /// <summary>
    /// 创建存储服务实例。
    /// </summary>
    /// <param name="dataDirectory">数据目录路径；为空时使用 %LOCALAPPDATA%\DshManager\Data。</param>
    public JsonStoreService(string? dataDirectory = null)
    {
        // 默认使用用户级数据目录（见 GetDefaultDataDirectory 说明），
        // 避免安装到 Program Files 后因无写权限导致保存失败
        _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? GetDefaultDataDirectory()
            : dataDirectory;

        // 统一封装序列化选项：缩进输出便于人工阅读与调试；关闭 HTML 转义（保留中文/单引号原样，避免 \u0027 等噪音）
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }

    /// <summary>数据目录绝对路径（供其他组件解析相对路径时复用）。</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>
    /// 计算默认数据目录：优先 %LOCALAPPDATA%\DshManager\Data
    /// （面板安装到 Program Files 后仍具备写权限，保证设置/记忆库等能正常持久化），
    /// 取不到该环境变量时回退到程序目录下的 Data。
    /// </summary>
    private static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(localAppData, "DshManager", "Data");
    }

    /// <summary>
    /// 从指定文件名加载数据；文件不存在或内容损坏时返回默认值（new T()）。
    /// </summary>
    /// <typeparam name="T">数据类型（需有无参构造函数）。</typeparam>
    /// <param name="fileName">文件名，例如 "settings.json"。</param>
    public T Load<T>(string fileName) where T : class, new()
    {
        lock (_saveLock)
        {
            return LoadCore<T>(fileName);
        }
    }

    /// <summary>
    /// 在写锁内完成"读 → 改 → 写"，用于只改其中几个字段的场景。
    /// 【为什么必须有】调用方各自 Load + 改 + Save 时，两次操作之间可能被另一个写入方插入，
    /// 后写的会把先写的改动整段覆盖（settings.json 有三个写入方，实测会丢锁定安装路径）。
    /// 把整个"读-改-写"放进同一把锁，才能保证不丢字段。
    /// </summary>
    /// <typeparam name="T">数据类型（需有无参构造函数）。</typeparam>
    /// <param name="fileName">文件名。</param>
    /// <param name="mutate">对已加载对象的修改动作。</param>
    public void Update<T>(string fileName, Action<T> mutate) where T : class, new()
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));

        lock (_saveLock)
        {
            var current = LoadCore<T>(fileName);
            mutate(current);
            SaveCore(fileName, current);
        }
    }

    /// <summary>无锁读取实现：调用方必须已持有 <see cref="_saveLock"/>。</summary>
    private T LoadCore<T>(string fileName) where T : class, new()
    {
        var filePath = GetFilePath(fileName);
        if (!File.Exists(filePath))
        {
            // 文件不存在：直接返回默认值，不抛异常
            return new T();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            // 反序列化失败（JSON 损坏或类型不匹配）时由 catch 回退默认值
            return JsonSerializer.Deserialize<T>(json, _jsonOptions) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 【为什么 catch 三种】本类注释承诺"文件损坏/读取失败时回退默认值，保证应用不崩溃"：
            // - JsonException：内容损坏或类型不匹配；
            // - IOException：文件正被其它进程占用（dsh 自身或编辑器）、磁盘错误；
            // - UnauthorizedAccessException：权限不足。
            // 只 catch JsonException 时后两种会一路冒泡到 UI 线程并终止进程。
            return new T();
        }
    }

    /// <summary>
    /// 将数据保存到指定文件名（自动创建目录）。
    /// 采用"写临时文件 → 原子替换"并加锁，保证并发写入不丢数据、断电不产生半个 JSON。
    /// </summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="fileName">文件名，例如 "settings.json"。</param>
    /// <param name="data">要保存的数据。</param>
    public void Save<T>(string fileName, T data)
    {
        // 【为什么要锁】settings.json 有三个写入方（安装位置发现、设置页、记忆页），
        // 各自都是"读-改-写"：不加锁会互相覆盖，典型表现是刚设置的锁定安装路径莫名丢失。
        lock (_saveLock)
        {
            SaveCore(fileName, data);
        }
    }

    /// <summary>无锁写入实现：调用方必须已持有 <see cref="_saveLock"/>。</summary>
    private void SaveCore<T>(string fileName, T data)
    {
        var filePath = GetFilePath(fileName);

        // 确保数据目录存在，否则写入会抛 DirectoryNotFoundException
        Directory.CreateDirectory(_dataDirectory);

        var json = JsonSerializer.Serialize(data, _jsonOptions);

        // 先写临时文件，再原子替换：
        // 直接 File.WriteAllText 会在写入中途断电/崩溃时留下截断的 JSON，
        // 下次启动读到半个文件只能静默回退默认值（用户表现为设置全部丢失）。
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);

        // File.Move(overwrite: true) 在同一卷上是原子替换（MoveFileEx + REPLACE_EXISTING）
        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <summary>
    /// 拼接文件名与数据目录，得到完整文件路径。
    /// </summary>
    private string GetFilePath(string fileName)
    {
        return Path.Combine(_dataDirectory, fileName);
    }
}
