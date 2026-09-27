using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>界面上的一个参数分组（6 个分组之一）。</summary>
public sealed class ConfigGroup
{
    public string Name { get; }
    public IReadOnlyList<ConfigFieldValue> Fields { get; }

    public ConfigGroup(string name, IReadOnlyList<ConfigFieldValue> fields)
    {
        Name = name;
        Fields = fields;
    }
}

/// <summary>
/// 配置页视图模型：按 6 个分组展示 DSH 官方设置项（共 33 项），
/// 读写 &lt;DSH_HOME&gt;/settings.yaml。
///
/// 关键设计：保存时只对"改过的项"调用服务的逐项最小改写，未改的键一个字节都不动，
/// 因此官方新增的键与注释、以及 providers 这类复杂结构都原样保留。
/// </summary>
public partial class ConfigViewModel : ObservableObject
{
    private readonly DshConfigService _service;
    private readonly List<ConfigFieldValue> _fields = new();

    /// <summary>按分组组织的表单字段（页面按此分节展示）。</summary>
    public ObservableCollection<ConfigGroup> Groups { get; } = new();

    /// <summary>底部状态提示（加载 / 保存 / 重置结果）。</summary>
    [ObservableProperty]
    private string statusMessage = "";

    /// <summary>settings.yaml 的实际路径（页脚展示，方便用户定位）。</summary>
    public string SettingsPath => _service.SettingsPath;

    public ConfigViewModel() : this(null)
    {
    }

    /// <summary>可注入配置服务（便于测试）；默认指向真实 DSH 数据目录。</summary>
    public ConfigViewModel(DshConfigService? service)
    {
        _service = service ?? new DshConfigService();
        BuildGroups();
        LoadIntoForm();
        StatusMessage = $"已加载 {SettingsPath}（共 {_fields.Count} 项参数）";
    }

    /// <summary>按 6 个分组把 33 项参数包装成可绑定的字段。</summary>
    private void BuildGroups()
    {
        Groups.Clear();
        _fields.Clear();

        foreach (var groupName in ConfigField.Groups())
        {
            var values = new List<ConfigFieldValue>();
            foreach (var meta in ConfigField.InGroup(groupName))
            {
                var value = new ConfigFieldValue(meta);
                values.Add(value);
                _fields.Add(value);
            }
            Groups.Add(new ConfigGroup(groupName, values));
        }
    }

    /// <summary>从磁盘重新读取并填充表单（丢弃未保存改动）。</summary>
    private void LoadIntoForm()
    {
        _service.Reload();
        foreach (var field in _fields)
        {
            field.LoadValue(field.Meta.Kind == ParamKind.Complex
                ? BuildComplexPreview(field.Meta)
                : _service.ReadValue(field.Meta.Path));
        }
    }

    /// <summary>
    /// 复杂结构（providers / allowedModels）在界面上只读展示。
    /// providers 额外整理成"每条服务商一段"的可读文本，便于看清已配置的 api / baseURL / apiKeyEnv / models。
    /// </summary>
    private string BuildComplexPreview(ConfigField meta)
    {
        var raw = _service.ReadValue(meta.Path) ?? "";
        if (meta.Path != "llm-pi-ai.providers") return raw;

        var providers = _service.ReadProviders();
        if (providers.Count == 0) return raw.Length == 0 ? "{}（尚未配置任何第三方服务商）" : raw;

        var sb = new StringBuilder();
        foreach (var p in providers)
        {
            sb.Append(p.Id).Append("　[").Append(p.Api).Append("]\n");
            sb.Append("  baseURL: ").Append(p.BaseURL).Append('\n');
            sb.Append("  apiKeyEnv: ").Append(p.ApiKeyEnv).Append('\n');
            sb.Append("  models: ").Append(p.Models).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 保存：只把改过的项逐项最小改写进 settings.yaml，然后备份 + 原子落盘。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        var errors = new List<string>();

        foreach (var field in _fields)
        {
            // 复杂结构面板只读，需手工编辑 settings.yaml
            if (field.Meta.Kind == ParamKind.Complex) continue;
            if (!field.IsChanged) continue;   // 未改的键不碰

            var value = field.ToStorageValue();
            if (field.Meta.Kind == ParamKind.Number && value.Length > 0 && !IsNumber(value))
            {
                errors.Add($"「{field.Meta.Title}」不是有效数字：{value}");
                continue;
            }

            // 布尔/数字原样写入（不加引号），其余按需加引号；空值表示删除该键
            var ok = field.Meta.Kind is ParamKind.Bool or ParamKind.Number
                ? _service.SetRawValue(field.Meta.Path, value)
                : _service.SetValue(field.Meta.Path, value);

            if (!ok) errors.Add($"「{field.Meta.Title}」写入失败：{_service.LastError}");
        }

        if (errors.Count > 0)
        {
            StatusMessage = "保存失败：" + string.Join("；", errors);
            return;
        }

        if (!_service.IsDirty)
        {
            StatusMessage = "没有需要保存的改动";
            return;
        }

        var backup = _service.Save();
        if (backup is null)
        {
            // 外部冲突时给出可执行的下一步，而不是一句干巴巴的"保存失败"
            StatusMessage = _service.HasExternalConflict
                ? "未保存：settings.yaml 已被外部修改，请先点「重新加载」查看对方改动，再重新应用你的修改"
                : "保存失败：" + _service.LastError;
            return;
        }

        // 保存后以磁盘内容为新基准（"已改"角标随之清空）
        LoadIntoForm();
        StatusMessage = backup.Length == 0
            ? "已保存 ✓"
            : $"已保存 ✓（原文件已备份为 {Path.GetFileName(backup)}）";
    }

    /// <summary>重新加载：丢弃未保存的改动，回到文件里的值。</summary>
    [RelayCommand]
    private void Reload()
    {
        LoadIntoForm();
        StatusMessage = "已重新加载（未保存的改动已丢弃）";
    }

    /// <summary>恢复默认：只把表单重置为参数默认值，不写盘；需再点保存才生效。</summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        foreach (var field in _fields) field.ResetToDefault();
        StatusMessage = "已恢复默认值，需点击「保存」后才写入文件";
    }

    /// <summary>数字校验：接受整数与小数（这些参数都是超时/字节数/字号，非负）。</summary>
    private static bool IsNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
        && !double.IsNaN(n) && !double.IsInfinity(n);
}
