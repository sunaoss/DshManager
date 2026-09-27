using System.Diagnostics;
using System.IO;

namespace DshManager.Services;

/// <summary>
/// 表情包服务：统一管理「内置表情」与「用户自定义表情」两个目录。
/// - 内置目录：&lt;程序目录&gt;\Assets\Stickers（随安装包发布，只读语义，不允许删除/修改）；
/// - 用户目录：%LOCALAPPDATA%\DshManager\Stickers（可写，用于用户自己添加的表情）。
/// 所有方法都做了异常兜底：失败时返回 null / false 并通过 message 给出中文原因，绝不把异常抛到界面。
/// </summary>
public class StickerService
{
    /// <summary>允许添加的图片扩展名（统一小写比较）。</summary>
    private static readonly string[] AllowedExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    /// <summary>内置表情目录：&lt;程序目录&gt;\Assets\Stickers（随安装包发布，只读语义）</summary>
    public string BuiltInDirectory { get; }

    /// <summary>用户表情目录：%LOCALAPPDATA%\DshManager\Stickers（可写，自动创建）</summary>
    public string UserDirectory { get; }

    /// <summary>页面默认构造：内置目录取程序目录，用户目录取本地应用数据目录。</summary>
    public StickerService() : this(
        Path.Combine(AppContext.BaseDirectory, "Assets", "Stickers"),
        GetDefaultUserDirectory())
    {
    }

    /// <summary>注入式构造（测试用）：允许指定两个目录，便于用临时目录验证逻辑。</summary>
    public StickerService(string builtInDirectory, string userDirectory)
    {
        BuiltInDirectory = builtInDirectory;
        UserDirectory = userDirectory;
        EnsureUserDirectory();
    }

    /// <summary>计算默认用户表情目录：%LOCALAPPDATA%\DshManager\Stickers，取不到时回退程序目录。</summary>
    private static string GetDefaultUserDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "Stickers")
            : Path.Combine(localAppData, "DshManager", "Stickers");
    }

    /// <summary>列出全部可用表情（用户目录优先 + 内置目录，按文件名去重、按文件名排序）。返回绝对路径。</summary>
    public IReadOnlyList<string> ListAll()
    {
        // 以「文件名」为键去重：先收用户目录（同名时用户版本胜出），再收内置目录（同名跳过）
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectFiles(UserDirectory, byFileName);
        CollectFiles(BuiltInDirectory, byFileName);

        // 最后按文件名统一排序，保证界面上的展示顺序稳定（不依赖文件系统的枚举顺序）
        return byFileName.Values
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把一个本地图片文件复制进用户表情目录（保留扩展名、重名自动加序号）；失败时返回 null + 中文原因。</summary>
    public string? AddSticker(string sourceFilePath, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                message = "源文件不存在，无法添加。";
                return null;
            }

            var extension = Path.GetExtension(sourceFilePath);
            // 扩展名限制为图片类型：避免用户把 txt/exe 等无关文件混进表情目录
            if (!AllowedExtensions.Contains(extension.ToLowerInvariant()))
            {
                message = $"不支持的图片格式（{extension}），仅支持 png/jpg/jpeg/gif/webp/bmp。";
                return null;
            }

            EnsureUserDirectory();

            // 同名的已存在文件绝不覆盖，自动向后找 name-2、name-3 … 作为新文件名
            var targetPath = BuildUniquePath(Path.GetFileNameWithoutExtension(sourceFilePath), extension);
            File.Copy(sourceFilePath, targetPath);
            message = "添加成功。";
            return targetPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            message = $"添加失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>删除表情：仅允许删除用户目录下的文件；内置目录的文件拒绝删除（message 说明原因）。</summary>
    public bool DeleteSticker(string filePath, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                message = "表情文件不存在或已被删除。";
                return false;
            }

            // 先判断内置目录：内置表情随安装包发布，只读语义，任何情况下都不允许删除
            if (IsUnderDirectory(filePath, BuiltInDirectory))
            {
                message = "内置表情不可删除。";
                return false;
            }

            // 再限制只能删用户目录内的文件，避免误删用户其它位置的文件
            if (!IsUnderDirectory(filePath, UserDirectory))
            {
                message = "只能删除用户表情目录下的表情。";
                return false;
            }

            File.Delete(filePath);
            message = "已删除。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            message = $"删除失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>用资源管理器打开用户表情目录（不存在则创建）。</summary>
    public void OpenUserDirectory()
    {
        try
        {
            EnsureUserDirectory();
            // UseShellExecute = true 才会交给系统默认程序（资源管理器）打开该目录
            Process.Start(new ProcessStartInfo
            {
                FileName = UserDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开失败（如系统限制）不影响聊天，静默处理
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>确保用户目录存在（幂等，失败时静默：后续写入会各自报错）。</summary>
    private void EnsureUserDirectory()
    {
        try
        {
            Directory.CreateDirectory(UserDirectory);
        }
        catch
        {
            // 目录创建失败不抛异常，交由具体操作返回中文原因
        }
    }

    /// <summary>收集目录下的图片文件；已在字典中的同名文件跳过（实现「用户目录优先」）。</summary>
    private static void CollectFiles(string directory, Dictionary<string, string> byFileName)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory))
            {
                if (!AllowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    continue;
                }

                var fileName = Path.GetFileName(file);
                // 已存在同名（大小写不敏感）时不覆盖：先收的目录优先级更高
                byFileName.TryAdd(fileName, file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录不可读时跳过，不影响聊天
        }
    }

    /// <summary>在用户目录内构造一个不冲突的文件路径：name.ext → name-2.ext → name-3.ext …</summary>
    private string BuildUniquePath(string baseName, string extension)
    {
        var candidate = Path.Combine(UserDirectory, baseName + extension);
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(UserDirectory, $"{baseName}-{index}{extension}");
            index++;
        }

        return candidate;
    }

    /// <summary>判断文件是否位于指定目录内（比较规范化的绝对路径，大小写不敏感以适配 Windows）。</summary>
    private static bool IsUnderDirectory(string filePath, string directory)
    {
        try
        {
            var fullDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFile = Path.GetFullPath(filePath);

            return fullFile.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullFile, fullDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 路径非法时按「不在目录内」处理，交由上层返回拒绝原因
            return false;
        }
    }
}
