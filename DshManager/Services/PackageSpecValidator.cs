using System;
using System.Collections.Generic;

namespace DshManager.Services;

/// <summary>
/// 插件包规格（&lt;包名&gt;[&lt;分隔符&gt;&lt;版本/标签&gt;]）的校验与规范化。
///
/// 【为什么单独抽一个类】
/// 面板有两条路径都会把「包名」拼进 node 的 argv：
///   ① 市场页：包名/版本来自 registry 返回的 JSON（外部数据）；
///   ② 插件页安装输入框：包名来自用户手输。
/// 两条路径都必须过同一道防线，否则会出现「市场页挡住了、手输框还能注入」的缺口。
/// 合法包名不可能含空格、引号、路径分隔符或 <c>#</c>，所以直接拒绝最稳。
///
/// 【被挡住的是什么】
/// pnpm 会把空格后的 token 当成独立选项解析，因此形如
/// <c>foo --registry=http://evil</c> 的「包名」能改写安装源，让面板从攻击者仓库装包
/// 并执行它的安装脚本（dsh plugin add 走的是 node&lt;bin.js&gt;，UseShellExecute=false，
/// 所以不存在 shell 元字符注入，但参数注入是真实成立的）。
/// </summary>
public static class PackageSpecValidator
{
    /// <summary>npm 包名长度上限（registry 规范）。</summary>
    private const int MaxPackageNameLength = 214;

    /// <summary>版本/标签字符串长度上限。</summary>
    private const int MaxVersionLength = 128;

    /// <summary>校验 npm 包名：允许 <c>name</c> 或 <c>@scope/name</c>，字母数字与 <c>._-~</c>。</summary>
    public static bool IsSafePackageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxPackageNameLength) return false;

        // 全局 trim 后前导/尾随空格会让 pnpm 把 token 切开，直接拒绝
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return false;

        var scoped = name.StartsWith('@');
        var body = scoped ? name[1..] : name;
        if (body.Length == 0) return false;

        foreach (var ch in body)
        {
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '~' or '/';
            if (!ok) return false;
        }

        // 作用域包形如 @scope/name：斜杠至多一个，且不能出现在首尾
        var slashCount = 0;
        foreach (var ch in body)
        {
            if (ch == '/') slashCount++;
        }

        if (body.StartsWith('/') || body.EndsWith('/')) return false;
        return scoped ? slashCount == 1 : slashCount == 0;
    }

    /// <summary>校验版本/标签字符串：只允许 semver 与常见 tag 字符，禁止空格与引号。</summary>
    public static bool IsSafeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > MaxVersionLength) return false;
        if (!string.Equals(version, version.Trim(), StringComparison.Ordinal)) return false;

        foreach (var ch in version)
        {
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+' or '_' or '~' or '^' or '*' or '>';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>
    /// 校验一条安装规格。<paramref name="name"/> 是包名，<paramref name="version"/> 是版本/标签
    /// （无版本时为空串），<paramref name="separator"/> 是原有的分隔符（<c>@</c> 或空串），
    /// 便于调用方原样回显用户输入。
    /// </summary>
    /// <remarks>
    /// 兼容两种写法：<c>name@1.2.3</c>（分隔符 <c>@</c>）与 <c>name#1.2.3</c>（分隔符 <c>#</c>，
    /// 保留旧行为）。都没有版本时按整串当包名校验。
    /// </remarks>
    public static bool TryValidateSpec(string? spec, out string name, out string version, out string separator)
    {
        name = string.Empty;
        version = string.Empty;
        separator = string.Empty;

        var trimmed = spec?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;

        // 整串就是一个合法包名（含 @scope/name 这种以 @ 开头的情况）
        if (IsSafePackageName(trimmed))
        {
            name = trimmed;
            return true;
        }

        // '#' 是历史上用过的版本分隔符，只在能拆出「合法包名 + 合法版本」时才认
        var hashIndex = trimmed.IndexOf('#');
        if (hashIndex > 0)
        {
            var head = trimmed[..hashIndex];
            var tail = trimmed[(hashIndex + 1)..];
            if (IsSafePackageName(head) && IsSafeVersion(tail))
            {
                name = head;
                version = tail;
                separator = "#";
                return true;
            }
        }

        // '@' 版本分隔符：只认最后一个 '@'（作用域包的名字本身以 '@' 开头）
        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex > 0)
        {
            var head = trimmed[..atIndex];
            var tail = trimmed[(atIndex + 1)..];
            if (IsSafePackageName(head) && IsSafeVersion(tail))
            {
                name = head;
                version = tail;
                separator = "@";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把以空白分隔的多条安装规格逐条校验，成功时返回规范化后的规格列表
    /// （保持原有顺序，丢弃多余空项）。
    /// </summary>
    /// <remarks>
    /// 插件页的安装输入框明确支持「多个包，空格分隔」，所以不能对整串一刀切拒绝：
    /// 那样会把合法的 <c>dsh-a dsh-b</c> 也挡掉。这里逐条校验，任一条不合法就整体拒绝
    /// —— 部分安装比不安装更糟（用户以为装全了）。
    /// </remarks>
    public static bool TryValidateSpecList(string? input, out List<string> specs, out string invalidToken)
    {
        specs = new List<string>();
        invalidToken = string.Empty;

        var raw = input ?? string.Empty;
        var tokens = raw.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0) return false;

        foreach (var token in tokens)
        {
            if (!TryValidateSpec(token, out var name, out var version, out var separator))
            {
                invalidToken = token;
                specs.Clear();
                return false;
            }

            specs.Add(separator.Length == 0 ? name : name + separator + version);
        }

        return specs.Count > 0;
    }
}
