using System.IO;
using System.Text;

namespace DshManager.Services;

/// <summary>
/// AGENTS.md 中的一个「## 小节」——即记忆库界面左侧的一组条目。
/// </summary>
public class MemorySection
{
    /// <summary>小节标题（不含 "## " 前缀），如「关于我」。</summary>
    public string Heading { get; set; } = string.Empty;

    /// <summary>小节下的条目正文，统一为「不含 "- " 前缀」形式。</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>
    /// 条目在原文中的 0 基行号（与 <see cref="Items"/> 一一对应），-1 表示内存中新建、原文暂无对应行。
    /// 作为「可定位标识」，供删除 / 编辑定位与调试使用；
    /// 增删条目请走 <see cref="AddItem"/> / <see cref="RemoveItemAt"/>，以保证两张表始终一一对应。
    /// </summary>
    public List<int> SourceLines { get; set; } = new();

    /// <summary>追加一条条目，并记录其原文行号（保持两表一一对应）。</summary>
    public void AddItem(string text, int sourceLine = -1)
    {
        Items.Add(text);
        SourceLines.Add(sourceLine);
    }

    /// <summary>按条目正文定位下标（首个匹配）；找不到返回 -1。</summary>
    public int IndexOfItem(string? text)
    {
        var key = text?.Trim() ?? string.Empty;
        return Items.FindIndex(i => string.Equals(i, key, StringComparison.Ordinal));
    }

    /// <summary>删除指定下标处的条目（两表同步删除）；下标越界返回 false。</summary>
    public bool RemoveItemAt(int index)
    {
        if (index < 0 || index >= Items.Count) return false;
        Items.RemoveAt(index);
        if (index < SourceLines.Count) SourceLines.RemoveAt(index);
        return true;
    }
}

/// <summary>
/// 一份完整的 AGENTS.md 文档模型：一级标题 + "&gt;" 引用说明段 + 若干 "##" 小节。
/// </summary>
public class MemoryDocument
{
    /// <summary>一级标题（不含 "# " 前缀），如「我的长期记忆」。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 文件顶部的 "&gt;" 引用说明段，按原样保存（每行含 "&gt;" 前缀），多行以 '\n' 分隔。
    /// </summary>
    public string Intro { get; set; } = string.Empty;

    /// <summary>全部小节，顺序与文件中的出现顺序一致。</summary>
    public List<MemorySection> Sections { get; set; } = new();
}

/// <summary>
/// 记忆文件字节预算信息：dsh 注入的字节预算为 65536（64KB）。
/// </summary>
public sealed class MemoryBudgetInfo
{
    /// <summary>AGENTS.md 当前占用的字节数（文件不存在时为 0）。</summary>
    public int Bytes { get; init; }

    /// <summary>dsh 的字节预算（65536）。</summary>
    public int BudgetBytes { get; init; }

    /// <summary>占用百分比（可能大于 100，表示已超预算）。</summary>
    public double Percent => BudgetBytes <= 0 ? 0 : Bytes * 100.0 / BudgetBytes;

    /// <summary>是否已超出字节预算。</summary>
    public bool IsOverBudget => Bytes > BudgetBytes;
}

/// <summary>
/// 记忆库服务：管理 DSH 真正会读取的长期记忆文件 <c>&lt;DSH_HOME&gt;\AGENTS.md</c>。
///
/// dsh 自带的 dsh-agent-instructions 插件会在每次新会话首次请求时读取该文件并注入上下文，
/// 因此写入此文件的内容才会真正影响 dsh 的行为（旧的 memory.json 与 dsh 毫无关联，已废弃）。
///
/// 写入约定：
/// ① 任何写操作前都先把原文件备份为 <c>AGENTS.md.bak.yyyyMMdd-HHmmss</c>（同名不覆盖），
///    并只保留最近若干份备份；
/// ② 追加条目时小节标题已存在则不重复添加，正文逐行裁剪（以 "-"/"#" 开头保留原样，否则补 "- " 前缀），
///    从而不会触碰用户手写的既有段落；
/// ③ 统一使用 UTF8Encoding(false)（无 BOM）写盘，换行统一为 '\n'。
/// </summary>
public class MemoryLibraryService
{
    /// <summary>记忆文件名（dsh 默认加载的候选名之一）。</summary>
    public const string MemoryFileName = "AGENTS.md";

    /// <summary>dsh 给这份注入内容的字节预算（agent-instructions 的 config.maxBytes）。</summary>
    public const int BudgetBytes = 65536;

    /// <summary>备份文件最多保留份数，超出后删除最旧的。</summary>
    private const int MaxBackups = 10;

    private readonly string _dshHome;

    /// <summary>使用真实环境的数据目录（$DSH_HOME，否则 %USERPROFILE%\.dsh）创建服务。</summary>
    public MemoryLibraryService() : this(DshDiscoveryService.GetDefaultDshHome())
    {
    }

    /// <summary>注入数据目录（便于单元测试使用临时目录，绝不触碰真实 ~/.dsh）。</summary>
    public MemoryLibraryService(string? dshHome)
    {
        _dshHome = string.IsNullOrWhiteSpace(dshHome) ? DshDiscoveryService.GetDefaultDshHome() : dshHome;
    }

    /// <summary>dsh 数据目录。</summary>
    public string DshHome => _dshHome;

    /// <summary>记忆文件完整路径：&lt;DSH_HOME&gt;\AGENTS.md。</summary>
    public string MemoryFilePath => Path.Combine(_dshHome, MemoryFileName);

    /// <summary>最近一次写操作生成的备份文件路径；未产生备份（或首次创建文件）时为 null。</summary>
    public string? LastBackupPath { get; private set; }

    /// <summary>
    /// 上次备份失败的原因；空串表示没有失败。
    /// 供 UI 在保存被拒时告诉用户"到底是备份失败了还是写失败了"。
    /// </summary>
    public string BackupError { get; private set; } = "";

    // ==================== 读取 / 解析 ====================

    /// <summary>
    /// 解析 AGENTS.md：文件不存在返回空文档；结构不标准时尽力解析，任何异常都不抛给调用方。
    /// </summary>
    public MemoryDocument Load()
    {
        var document = new MemoryDocument();
        try
        {
            if (!File.Exists(MemoryFilePath)) return document;
            ParseInto(document, File.ReadAllText(MemoryFilePath, Encoding.UTF8));
        }
        catch
        {
            // 读取/解析失败（权限、占用、编码异常等）→ 退化为空文档，保证 UI 不崩
        }
        return document;
    }

    /// <summary>把文件文本解析进文档模型（容错：非标准行按条目处理，绝不抛异常）。</summary>
    private static void ParseInto(MemoryDocument document, string text)
    {
        var lines = NormalizeNewlines(text).Split('\n');
        MemorySection? current = null;
        var introLines = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;   // 空行仅作分隔，不入模型

            var hashes = LeadingHashCount(trimmed);
            if (hashes == 1)
            {
                // 首个一级标题 → 文档标题；若已在小节内，则当作普通条目原样保留
                if (current is null && document.Title.Length == 0)
                {
                    document.Title = trimmed.TrimStart('#').Trim();
                }
                else
                {
                    current?.AddItem(trimmed, i);
                }
                continue;
            }

            if (hashes >= 2)
            {
                // "## 小节"：新建分组（### 等多级标题也归入小节，尽力解析）
                current = new MemorySection { Heading = trimmed.TrimStart('#').Trim() };
                document.Sections.Add(current);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                // ">" 引用段只在小节之前有意义；小节内出现则按条目处理
                if (current is null) introLines.Add(raw);
                else current.AddItem(TrimBulletPrefix(trimmed), i);
                continue;
            }

            if (current is null)
            {
                // 首个 "##" 之前的散落文本：并入前言，避免丢内容
                introLines.Add(raw);
                continue;
            }

            current.AddItem(TrimBulletPrefix(trimmed), i);
        }

        document.Intro = string.Join('\n', introLines).Trim();
    }

    /// <summary>换行统一成 '\n'（同时兼容 CRLF 与单独的 CR）。</summary>
    public static string NormalizeNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // ==================== 写入 ====================

    /// <summary>
    /// 整份覆盖写回 AGENTS.md（写前先备份），保持模型中的结构与顺序。
    /// </summary>
    /// <returns>true = 写入成功；false = 写入失败（目录不可写、文件被占用等）。</returns>
    public bool Save(MemoryDocument document)
    {
        if (document is null) return false;
        BackupError = "";
        try
        {
            var dir = Path.GetDirectoryName(MemoryFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 备份失败必须中止：本次是整份覆盖写，没有备份就等于用户的长期记忆
            // 在"无安全网"状态下被写掉（磁盘满 / AGENTS.md 被占用时尤其危险）。
            var hadFile = File.Exists(MemoryFilePath);
            BackupCurrent();
            if (hadFile && LastBackupPath is null)
            {
                BackupError = string.IsNullOrEmpty(BackupError) ? "备份失败" : BackupError;
                return false;
            }

            File.WriteAllText(MemoryFilePath, BuildContent(document), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(BackupError)) BackupError = ex.Message;
            return false;
        }
    }

    /// <summary>按规范格式拼出文件正文（一级标题、引用段、各小节与条目）。</summary>
    private static string BuildContent(MemoryDocument document)
    {
        var builder = new StringBuilder();

        var title = document.Title.Trim();
        if (title.Length > 0) builder.Append("# ").Append(title).Append('\n').Append('\n');

        var intro = document.Intro.Trim();
        if (intro.Length > 0) builder.Append(intro).Append('\n').Append('\n');

        foreach (var section in document.Sections)
        {
            builder.Append("## ").Append(section.Heading.Trim()).Append('\n').Append('\n');
            foreach (var item in section.Items)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                var line = item.Trim();
                // 逐行裁剪：以 "-"/"#" 开头的行保留原样，否则补 "- " 前缀，避免双重前缀
                builder.Append(line.StartsWith('-') || line.StartsWith('#') ? line : "- " + line).Append('\n');
            }
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 覆盖前备份当前文件为 AGENTS.md.bak.yyyyMMdd-HHmmss（已存在则追加序号，保证同名不覆盖），
    /// 并把备份数量裁剪到最近 <see cref="MaxBackups"/> 份。
    /// </summary>
    private void BackupCurrent()
    {
        LastBackupPath = null;
        if (!File.Exists(MemoryFilePath)) return;   // 首次创建文件，无需备份

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = MemoryFilePath + ".bak." + stamp;
        var seq = 1;
        while (File.Exists(backup)) backup = MemoryFilePath + ".bak." + stamp + "-" + seq++;

        try
        {
            File.Copy(MemoryFilePath, backup);
            LastBackupPath = backup;
        }
        catch (Exception ex)
        {
            // 【为什么不在这里吞掉异常】备份是"整份覆盖 AGENTS.md"这一动作的唯一安全网：
            // 磁盘满 / 文件被占用时备份失败，调用方必须能知道。
            // 吞掉会让 Save 继续覆盖，用户的长期记忆在没有备份的情况下被写掉且毫无提示。
            BackupError = ex.Message;
            return;
        }

        PruneBackups();
    }

    /// <summary>备份只保留最近 N 份：文件名内嵌定宽时间戳，按名升序即时间升序。</summary>
    private void PruneBackups()
    {
        var dir = Path.GetDirectoryName(MemoryFilePath);
        if (string.IsNullOrEmpty(dir)) return;

        var backups = Directory.GetFiles(dir, MemoryFileName + ".bak.*");
        if (backups.Length <= MaxBackups) return;

        // 【为什么按名过滤】通配符会连用户手工建的备份一起匹配并静默删掉。
        // 本服务自己生成的备份一律形如 <name>.bak.yyyyMMdd-HHmmss[-序号]，
        // 只清理符合这个形状的，用户自己命名的备份不动。
        var managed = new List<string>();
        foreach (var path in backups)
        {
            if (IsManagedBackupName(Path.GetFileName(path)))
                managed.Add(path);
        }

        if (managed.Count <= MaxBackups) return;

        managed.Sort(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < managed.Count - MaxBackups; i++)
        {
            try { File.Delete(managed[i]); } catch { /* 删除失败忽略，下次再清 */ }
        }
    }

    /// <summary>
    /// 判断文件名是否为本服务生成的备份：<c>&lt;MemoryFileName&gt;.bak.&lt;8位日期&gt;-&lt;6位时间&gt;[-序号]</c>。
    /// 只认这种定宽时间戳形状，避免误删用户手工备份。
    /// </summary>
    private static bool IsManagedBackupName(string fileName)
    {
        var prefix = MemoryFileName + ".bak.";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = fileName[prefix.Length..];
        // 形如 20260927-233406 或 20260927-233406-1
        var dash = rest.IndexOf('-');
        if (dash != 8 || rest.Length < 15)
            return false;

        for (var i = 0; i < 8; i++)
            if (!char.IsAsciiDigit(rest[i])) return false;

        for (var i = 9; i < 15; i++)
            if (!char.IsAsciiDigit(rest[i])) return false;

        // 其后只允许 "-<数字>" 的序号后缀
        var tail = rest[15..];
        if (tail.Length == 0) return true;
        if (tail[0] != '-') return false;

        for (var i = 1; i < tail.Length; i++)
            if (!char.IsAsciiDigit(tail[i])) return false;

        return true;
    }

    // ==================== 小节 / 条目增删改 ====================

    /// <summary>追加一条（或多行）条目：小节已存在则直接追加，不存在则新建小节并追加到文件末尾。</summary>
    /// <returns>true = 写入成功。</returns>
    public bool AddItem(string heading, string text)
    {
        var name = heading?.Trim() ?? string.Empty;
        var items = SplitItemLines(text);
        if (name.Length == 0 || items.Count == 0) return false;

        var document = Load();
        var section = FindSection(document, name);
        if (section is null)
        {
            section = new MemorySection { Heading = name };
            document.Sections.Add(section);
        }

        foreach (var item in items) section.AddItem(item);
        return Save(document);
    }

    /// <summary>把小节内首个匹配的条目替换为 newText；小节或条目不存在时返回 false。</summary>
    public bool UpdateItem(string heading, string oldText, string newText)
    {
        var normalized = newText?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return false;

        var document = Load();
        var section = FindSection(document, heading);
        var index = section?.IndexOfItem(oldText) ?? -1;
        if (section is null || index < 0) return false;

        section.Items[index] = normalized;   // 保留原 SourceLines 定位信息
        return Save(document);
    }

    /// <summary>删除小节内首个匹配的条目；小节或条目不存在时返回 false。</summary>
    public bool RemoveItem(string heading, string text)
    {
        var document = Load();
        var section = FindSection(document, heading);
        var index = section?.IndexOfItem(text) ?? -1;
        if (section is null || index < 0) return false;

        section.RemoveItemAt(index);
        return Save(document);
    }

    /// <summary>新建一个小节（追加到末尾）；名称为空或已存在时返回 false。</summary>
    public bool AddSection(string heading)
    {
        var name = heading?.Trim() ?? string.Empty;
        if (name.Length == 0) return false;

        var document = Load();
        if (FindSection(document, name) is not null) return false;   // 已存在则不重复建标题

        document.Sections.Add(new MemorySection { Heading = name });
        return Save(document);
    }

    /// <summary>删除整个小节（连同其下条目）；小节不存在时返回 false。</summary>
    public bool RemoveSection(string heading)
    {
        var document = Load();
        var section = FindSection(document, heading);
        if (section is null) return false;

        document.Sections.Remove(section);
        return Save(document);
    }

    /// <summary>重命名小节；名称被其它小节占用或写入失败时返回 false。</summary>
    public bool RenameSection(string oldHeading, string newHeading)
    {
        var name = newHeading?.Trim() ?? string.Empty;
        if (name.Length == 0) return false;

        var document = Load();
        var section = FindSection(document, oldHeading);
        if (section is null) return false;

        // 目标名已被其它小节占用 → 拒绝，避免出现两个同名分组
        var renamed = !string.Equals(section.Heading, name, StringComparison.Ordinal);
        if (renamed && FindSection(document, name) is not null) return false;

        section.Heading = name;
        return Save(document);
    }

    /// <summary>读取 AGENTS.md 的字节数与 65536 预算的占用百分比（供界面显示进度）。</summary>
    public MemoryBudgetInfo GetBudgetInfo()
    {
        var bytes = 0;
        try
        {
            if (File.Exists(MemoryFilePath)) bytes = (int)new FileInfo(MemoryFilePath).Length;
        }
        catch
        {
            // 文件被占用/不可读时按 0 计，界面不崩
        }
        return new MemoryBudgetInfo { Bytes = bytes, BudgetBytes = BudgetBytes };
    }

    // ==================== 内部工具 ====================

    /// <summary>按标题定位小节（Ordinal 精确匹配）。</summary>
    private static MemorySection? FindSection(MemoryDocument document, string? heading)
    {
        var key = heading?.Trim() ?? string.Empty;
        return document.Sections.FirstOrDefault(s => string.Equals(s.Heading, key, StringComparison.Ordinal));
    }

    /// <summary>把一段文本按行拆成条目（去空行、去首尾空白），支持一次追加多条。</summary>
    private static List<string> SplitItemLines(string? text)
    {
        return NormalizeNewlines(text)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>去掉条目行首的 "-"/"- " 前缀，得到统一的无前缀正文。</summary>
    private static string TrimBulletPrefix(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('-')) text = text[1..].Trim();
        return text;
    }

    /// <summary>统计行首连续 '#' 的个数（用于判定一级标题 / 小节标题）。</summary>
    private static int LeadingHashCount(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '#') count++;
        return count;
    }
}
