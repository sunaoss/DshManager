using System;
using System.Collections.Generic;
using System.Text;

namespace DshManager.Services;

/// <summary>
/// 极简 YAML 编辑器：只做一件事——**按点路径改某个键的值，其余字节原样保留**。
///
/// 为什么不直接解析成对象再序列化回去：
/// DSH 的 settings.yaml 是官方文件，可能被新版 DSH 加了新键、也可能带注释。
/// 一旦整体重写，未知键和注释就没了，用户升个版本配置就被我们吃掉一块。
/// 所以这里只动目标那一行，别的行（包括缩进、注释、其它键、原有顺序）原样搬过去。
///
/// 支持范围：嵌套映射 + 标量值（settings.yaml 就是这个形状）。
/// 形如 `providers: {}` 或 `providers: {a: 1}` 的流式复杂值一律当成"整块字符串"，
/// 可以读出来显示，但不拆开改。
/// </summary>
public static class YamlTextEditor
{
    /// <summary>YAML 嵌套层级中的一层键。</summary>
    private sealed class Level
    {
        public int Indent;
        public string Key = "";
    }

    /// <summary>把文本拆成行，同时记住原来用的是 \n 还是 \r\n（写回时保持同样风格）。</summary>
    private static string[] SplitLines(string? text, out string newline)
    {
        newline = text is not null && text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>把行数组拼回文本；结尾是否有换行由数组是否含尾部空串决定，从而保持原结尾状态。</summary>
    private static string Join(string[] lines, string newline) => string.Join(newline, lines);

    /// <summary>
    /// 逐行扫一遍，回调每个"键行"。
    /// 键行才会出现在路径里；列表项（- xxx）不参与路径（providers 的 models 列表等不拆改）。
    /// </summary>
    /// <param name="onScalar">叶子键（冒号后有值），回调 (行号, 完整路径, 值文本)。</param>
    /// <param name="onMapping">映射头（冒号后没值），回调 (行号, 完整路径)。</param>
    private static void Scan(string? text, Action<int, string, string>? onScalar, Action<int, string>? onMapping)
    {
        var lines = SplitLines(text, out _);
        var stack = new List<Level>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;   // 空行/注释行不参与路径

            var indent = line.Length - trimmed.Length;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed.Substring(0, colon).Trim();
            if (key.StartsWith("-", StringComparison.Ordinal)) continue;   // 列表项跳过

            var rest = trimmed.Substring(colon + 1).Trim();
            SplitInlineComment(rest, out var valueToken, out _);

            // 回退到当前缩进所属的父级
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);

            var path = new StringBuilder();
            foreach (var level in stack) path.Append(level.Key).Append('.');
            var full = path.Append(key).ToString();

            if (valueToken.Length == 0)
            {
                // 冒号后没值（含只有行尾注释）→ 这是映射头，压栈作为后续子键的父级
                onMapping?.Invoke(i, full);
                stack.Add(new Level { Indent = indent, Key = key });
            }
            else
            {
                onScalar?.Invoke(i, full, valueToken);
            }
        }
    }

    /// <summary>读出所有"路径 → 原始值文本"。复杂值（{}、[]）原样返回；行尾注释已剥离。</summary>
    public static Dictionary<string, string> Read(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Scan(text, (_, path, value) => map[path] = value, null);
        return map;
    }

    /// <summary>值是不是"复杂结构"（以 { 或 [ 开头的流式值）。</summary>
    public static bool IsComplex(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue)) return false;
        var v = rawValue.Trim();
        return v.StartsWith("{", StringComparison.Ordinal) || v.StartsWith("[", StringComparison.Ordinal);
    }

    /// <summary>去掉 YAML 字符串两边的引号，并把转义还原；非字符串原样返回。</summary>
    public static string Unquote(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue)) return "";
        var v = rawValue.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < v.Length - 1; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length - 1)
                {
                    var c = v[++i];
                    if (c == 'n') sb.Append('\n');
                    else if (c == 't') sb.Append('\t');
                    else if (c == 'r') sb.Append('\r');
                    else sb.Append(c);          // \" \\ 以及其它都按字面还原
                }
                else sb.Append(v[i]);
            }
            return sb.ToString();
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'')
            return v.Substring(1, v.Length - 2).Replace("''", "'");
        return v;
    }

    /// <summary>
    /// 该不该给值加引号。原则是"能不加就不加"——加得越少，写回去的差异越小，
    /// 官方文件里那一行就越接近原样。
    /// </summary>
    public static string QuoteIfNeeded(string? value)
    {
        var v = value ?? "";
        if (v.Length == 0) return "\"\"";

        var need = false;
        if (v[0] == ' ' || v[^1] == ' ') need = true;
        // YAML 里以这些字符开头的裸标量会被当成语法记号
        if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(v[0]) >= 0) need = true;
        // 冒号加空格、井号前有空格，都会被当成结构/注释
        if (v.Contains(": ", StringComparison.Ordinal)) need = true;
        if (v.Contains(" #", StringComparison.Ordinal)) need = true;
        foreach (var c in v)
        {
            if (c is '\n' or '\r' or '\t') { need = true; break; }
        }
        if (!need)
        {
            // 会被 YAML 解释成布尔/空值的裸词，必须加引号才能保成字符串
            var low = v.ToLowerInvariant();
            if (low is "null" or "true" or "false" or "yes" or "no" or "on" or "off" or "~") need = true;
        }
        if (!need) return v;
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }

    /// <summary>设置 a.b.c 的字符串值（按需加引号）。返回修改后的全文；无法写入时返回 null。</summary>
    public static string? Set(string? text, string path, string value) => SetRaw(text, path, QuoteIfNeeded(value));

    /// <summary>
    /// 设置 a.b.c 的值，值文本原样写入（用于布尔/数字等不应加引号的标量）。
    /// 有该键就改那一行；没有就新建——**包括把缺的父级一层层补齐**。
    /// </summary>
    public static string? SetRaw(string? text, string path, string rawValue)
    {
        if (string.IsNullOrEmpty(path)) return null;
        text ??= "";
        var lines = SplitLines(text, out var newline);

        // ① 已经有这个键：只改那一行的值（保留缩进与行尾注释）
        var hit = -1;
        Scan(text, (line, p, _) =>
        {
            if (p == path && hit < 0) hit = line;
        }, null);

        if (hit >= 0)
        {
            var line = lines[hit];
            var colon = line.IndexOf(':');
            var head = line.Substring(0, colon + 1) + " " + rawValue;

            var rest = line.Substring(colon + 1).Trim();
            SplitInlineComment(rest, out _, out var comment);
            if (comment.Length > 0) head += " " + comment;   // comment 已含开头的 #
            lines[hit] = head;
            return Join(lines, newline);
        }

        // ② 文件里还没有：找出最深的"已经存在的父级"
        var segs = path.Split('.');
        var knownDepth = 0;       // 已经存在几层父级
        var anchorLine = -1;      // 最深已存在父级的行号
        var anchorIndent = -1;

        for (var depth = segs.Length - 1; depth >= 1; depth--)
        {
            var prefix = string.Join(".", segs, 0, depth);
            var found = -1;
            var foundIndent = -1;
            Scan(text, null, (line, p) =>
            {
                if (p == prefix) { found = line; foundIndent = LeadingSpaces(lines[line]); }
            });
            if (found >= 0)
            {
                knownDepth = depth;
                anchorLine = found;
                anchorIndent = foundIndent;
                break;
            }
        }

        var add = new List<string>();
        int insertAt;
        int indent;
        var indentStep = 2;   // 默认步长；挂到已有父级时由 DetectIndentStep 按实际文件反推

        if (knownDepth == 0)
        {
            // 连顶层分节都没有：整条链追加到文件末尾（跳过尾部空行）
            insertAt = lines.Length;
            while (insertAt > 0 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
            indent = 0;
        }
        else
        {
            // 挂到已存在父级的块末尾：从锚点往下，把「比父级更深缩进的内容行」纳入父块。
            // 空行先记着跳过，但只有后面还跟着更深缩进的内容行时才算父块内容——
            // 否则文件结尾那个空行（换行标记）会被当成父块末尾，导致新键插到它后面：
            // 既多出一个空行，又把原有结尾换行弄丢。
            insertAt = anchorLine + 1;
            var blockEnd = anchorLine + 1;   // 父块最后一个内容行之后的位置（插入点）
            while (insertAt < lines.Length)
            {
                var t = lines[insertAt].TrimStart();
                if (t.Length == 0) { insertAt++; continue; }               // 空行：暂不认定属于父块
                if (LeadingSpaces(lines[insertAt]) <= anchorIndent) break; // 兄弟/上级节点：父块结束
                insertAt++;
                blockEnd = insertAt;                                       // 更深缩进的内容行属于父块
            }
            insertAt = blockEnd;

            // 【为什么不能写死 +2】缩进步长不是恒定的 2 —— YAML 规范没有规定步长，
            // 现有文件完全可能用 4 空格、8 空格甚至制表符。写死 +2 时，若父级用 4 空格，
            // 新键会被插到「比父级只深 2」的位置，YAML 解析器看到的是一个「不完整缩进」
            // 的层级：新键会挂到错误的分节下（甚至被当成父级同级），语义被静默改掉。
            // 这里从锚点块里现存的子行反推步长：锚点后第一个内容行相对锚点的缩进量即是本层步长。
            indentStep = DetectIndentStep(lines, anchorLine, anchorIndent);
            indent = anchorIndent + indentStep;
        }

        for (var i = knownDepth; i < segs.Length; i++)
        {
            add.Add(new string(' ', indent) + segs[i] + ":" +
                    (i == segs.Length - 1 ? " " + rawValue : ""));
            indent += indentStep;
        }

        var outLines = new List<string>(lines);
        outLines.InsertRange(insertAt, add);
        return Join(outLines.ToArray(), newline);
    }

    /// <summary>删掉 a.b.c 这一行（连同它的所有子行）。找不到就原样返回。</summary>
    public static string Remove(string? text, string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(text)) return text ?? "";
        var lines = SplitLines(text, out var newline);
        var keep = new List<string>();
        var stack = new List<Level>();
        var dropping = false;
        var dropIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (dropping)
            {
                // 最后一行若是空串，它只是"文件以换行结尾"的标记，不属于被删块——
                // 保留它才能让删光块内所有行后仍保持原有结尾换行。
                if (trimmed.Length == 0 && i == lines.Length - 1) dropping = false;
                else if (trimmed.Length == 0) continue;   // 块内空行一并删除
                else if (indent > dropIndent) continue;   // 仍属于被删块的子行
                else dropping = false;
            }

            if (trimmed.Length == 0 || trimmed[0] == '#') { keep.Add(line); continue; }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) { keep.Add(line); continue; }

            var key = trimmed.Substring(0, colon).Trim();
            if (key.StartsWith("-", StringComparison.Ordinal)) { keep.Add(line); continue; }

            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);

            var sb = new StringBuilder();
            foreach (var level in stack) sb.Append(level.Key).Append('.');
            var full = sb.Append(key).ToString();

            if (full == path || full.StartsWith(path + ".", StringComparison.Ordinal))
            {
                dropping = true;
                dropIndent = full == path ? indent : indent - 1;
                continue;
            }

            var rest = trimmed.Substring(colon + 1).Trim();
            if (rest.Length == 0) stack.Add(new Level { Indent = indent, Key = key });
            keep.Add(line);
        }
        return Join(keep.ToArray(), newline);
    }

    /// <summary>
    /// 把"冒号后的内容"拆成「值」与「行尾注释」。
    /// 引号值按配对引号定位注释；非引号值按"空格 + #"定位（YAML 规则）。
    /// </summary>
    private static void SplitInlineComment(string rest, out string value, out string comment)
    {
        comment = "";
        if (rest.Length == 0) { value = ""; return; }

        var quote = rest[0];
        if (quote == '"' || quote == '\'')
        {
            var i = 1;
            var closed = false;
            while (i < rest.Length)
            {
                if (quote == '"' && rest[i] == '\\') { i += 2; continue; }
                if (rest[i] == quote)
                {
                    if (quote == '\'' && i + 1 < rest.Length && rest[i + 1] == '\'') { i += 2; continue; }
                    closed = true;
                    i++;
                    break;
                }
                i++;
            }
            if (closed)
            {
                value = rest.Substring(0, i);
                var tail = rest.Substring(i);
                var hash = tail.IndexOf('#');
                if (hash >= 0) comment = tail.Substring(hash);
                return;
            }
            value = rest;   // 引号没闭合：整段当作值，保守处理
            return;
        }

        var idx = IndexOfInlineComment(rest);
        if (idx >= 0)
        {
            value = rest.Substring(0, idx).TrimEnd();
            comment = rest.Substring(idx);
        }
        else
        {
            value = rest;
        }
    }

    /// <summary>找"空格/tab + #"形式的行尾注释起点；找不到返回 -1。</summary>
    private static int IndexOfInlineComment(string s)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '#' && (s[i - 1] == ' ' || s[i - 1] == '\t')) return i;
        }
        return -1;
    }

    private static int LeadingSpaces(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    /// <summary>
    /// 从锚点块里现存的子行反推本层缩进步长：锚点后第一个非空内容行相对锚点的缩进差。
    /// 反推不出来（锚点下没有更深的内容行 / 缩进异常）时回退默认步长 2。
    /// </summary>
    /// <remarks>
    /// 【为什么以子行当作步长】YAML 没有规定缩进步长，文件可以统一用 2、4 或 8 空格。
    /// 锚点的直接子行相对锚点的缩进量，正是该文件在这一层实际使用的步长 —— 用它续写新键，
    /// 新键才会落在与既有同级键完全相同的位置。写死 +2 时若文件用 4 空格，
    /// 新键会被插到「比父级只深 2」的层级，YAML 解析结果与用户预期不符（挂错分节）。
    /// </remarks>
    private static int DetectIndentStep(string[] lines, int anchorLine, int anchorIndent)
    {
        const int defaultStep = 2;
        for (var i = anchorLine + 1; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.Length == 0) continue;                        // 空行跳过
            if (t.StartsWith('#')) continue;                    // 纯注释行不参与缩进推断

            var current = LeadingSpaces(lines[i]);
            if (current <= anchorIndent) break;                 // 走到兄弟/上级节点：锚点块已结束

            var step = current - anchorIndent;
            // 合法的步长必须为正且不荒谬（> 0 已在上面保证；上限防住 tab 混排等畸形缩进）
            return step is > 0 and <= 16 ? step : defaultStep;
        }

        return defaultStep;
    }
}
