using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 人格卡页视图模型：直接管理 dsh 官方的人格预设目录 &lt;DSH_HOME&gt;\.agent-presets\。
///
/// 1) 左侧列出全部人格卡（用户自建在前、随附只读卡在后，带「默认」「随附」徽章）；
/// 2) 右侧查看 / 编辑选中卡的名称、描述与 System Prompt（随附卡只读）；
/// 3) 「设为默认」写 settings.yaml 的 agent-presets.default —— 这才是 dsh 认可的启用方式，
///    不再是往 settings.yaml 塞一个 dsh 不认识的扁平键。
/// </summary>
public partial class PersonaViewModel : ObservableObject
{
    /// <summary>「无 dsh 自带模板」时提示文案里用的默认基底 id。</summary>
    private const string PreferredBaseId = "standard";

    private readonly PersonaService _service;

    /// <summary>全部人格卡（服务层已按「用户卡在前」的顺序返回）。</summary>
    public ObservableCollection<PersonaCard> Cards { get; } = [];

    /// <summary>「新建」时可选的模板基底（随附预设 + 已有用户卡）。</summary>
    public ObservableCollection<PersonaCard> BaseTemplates { get; } = [];

    /// <summary>当前选中的人格卡（选中即回填右侧表单）。</summary>
    [ObservableProperty]
    private PersonaCard? selectedCard;

    /// <summary>新建时选中的模板基底（决定复制谁的目录）。</summary>
    [ObservableProperty]
    private PersonaCard? selectedBaseTemplate;

    /// <summary>表单：人格卡 id（目录名，仅新建时可填）。</summary>
    [ObservableProperty]
    private string editId = string.Empty;

    /// <summary>表单：显示名。</summary>
    [ObservableProperty]
    private string editName = string.Empty;

    /// <summary>表单：一句话描述。</summary>
    [ObservableProperty]
    private string editDescription = string.Empty;

    /// <summary>表单：人格正文（写入 persona 块的 config.prefix）。</summary>
    [ObservableProperty]
    private string editSystemPrompt = string.Empty;

    /// <summary>状态提示（保存 / 新建 / 删除 / 设为默认的结果反馈）。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>当前新会话默认人格的文字摘要。</summary>
    [ObservableProperty]
    private string defaultSummary = string.Empty;

    /// <summary>环境提示（如找不到 dsh 自带模板时的降级说明）；为空则不显示。</summary>
    [ObservableProperty]
    private string templateHint = string.Empty;

    /// <summary>编辑区是否可用（选中可编辑卡或处于新建态）。</summary>
    [ObservableProperty]
    private bool isEditable;

    /// <summary>是否处于「新建」态（区别于编辑已有卡，用于放行 id 输入与基底选择）。</summary>
    [ObservableProperty]
    private bool isNewMode;

    /// <summary>页面默认构造：使用默认服务（真实 DSH_HOME 环境）。</summary>
    public PersonaViewModel() : this(new PersonaService())
    {
    }

    /// <summary>注入服务（便于测试），构造时加载已有卡片。</summary>
    public PersonaViewModel(PersonaService service)
    {
        _service = service;
        Reload(keepId: null);
    }

    // ---- 选中 / 状态联动 ----

    partial void OnSelectedCardChanged(PersonaCard? value)
    {
        if (value is not null)
        {
            IsNewMode = false;
            EditId = value.Id;
            EditName = value.Name;
            EditDescription = value.Description;
            EditSystemPrompt = value.SystemPrompt;
            IsEditable = !value.IsShipped;   // 随附卡只读：字段禁用，只能「另存为我的卡」
        }
        else if (!IsNewMode)
        {
            IsEditable = false;
        }

        NotifySelectionCommands();
    }

    partial void OnIsNewModeChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        CancelNewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>新建时切换模板基底 → 用该模板的内容预填表单，用户在此基础上改。</summary>
    partial void OnSelectedBaseTemplateChanged(PersonaCard? value)
    {
        if (!IsNewMode || value is null) return;
        EditId = PersonaService.SuggestId(value.Id) + "-copy";
        EditName = value.DisplayName + " 副本";
        EditDescription = value.Description;
        EditSystemPrompt = value.SystemPrompt;
    }

    // ---- 命令 ----

    /// <summary>进入新建态：解除选中，按当前基底预填表单。</summary>
    [RelayCommand]
    private void New()
    {
        SelectedCard = null;   // 触发 OnSelectedCardChanged（此时尚未置新建态，会先禁用编辑区）
        IsNewMode = true;
        IsEditable = true;

        var baseCard = SelectedBaseTemplate;
        if (baseCard is not null)
        {
            EditId = PersonaService.SuggestId(baseCard.Id) + "-copy";
            EditName = baseCard.DisplayName + " 副本";
            EditDescription = baseCard.Description;
            EditSystemPrompt = baseCard.SystemPrompt;
        }
        else
        {
            EditId = "persona";
            EditName = string.Empty;
            EditDescription = "我自己的人格";
            EditSystemPrompt = "你要用中文和我说话，简短、直接，像朋友一样。";
        }

        StatusText = "正在新建：选好「基础模板」，填 id / 显示名 / 人格正文，然后点「保存」。";
    }

    /// <summary>取消新建并回到普通浏览状态。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelNew))]
    private void CancelNew()
    {
        IsNewMode = false;
        Reload(keepId: null);
        StatusText = "已取消新建。";
    }

    private bool CanCancelNew() => IsNewMode;

    /// <summary>保存：新建态走 Create（复制模板目录），否则写回选中卡。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var name = EditName.Trim();
        var prompt = EditSystemPrompt.Trim();
        if (name.Length == 0)
        {
            StatusText = "保存失败：请填写显示名。";
            return;
        }
        if (prompt.Length == 0)
        {
            StatusText = "保存失败：请写一点人格正文，否则会话还是默认口吻。";
            return;
        }

        if (IsNewMode)
        {
            var id = EditId.Trim();
            if (id.Length == 0) id = PersonaService.SuggestId(name);

            var created = _service.Create(
                SelectedBaseTemplate?.Id ?? string.Empty, id, name, EditDescription.Trim(), prompt, out var error);
            if (created is null)
            {
                StatusText = "新建失败：" + error;
                return;
            }

            Reload(keepId: created.Id);
            StatusText = error.Length > 0
                ? error
                : $"已新建人格卡「{created.DisplayName}」（id：{created.Id}）；到 dsh 新建会话时即可选择它。";
            return;
        }

        var card = SelectedCard;
        if (card is null || card.IsShipped)
        {
            StatusText = "保存失败：随附预设是只读的，请先点「另存为我的卡」。";
            return;
        }

        card.Name = name;
        card.Description = EditDescription.Trim();
        card.SystemPrompt = prompt;
        if (!_service.Save(card, out var saveError))
        {
            StatusText = "保存失败：" + saveError;
            return;
        }

        Reload(keepId: card.Id);
        StatusText = $"已保存人格卡「{name}」。";
    }

    private bool CanSave() => IsNewMode || (SelectedCard is not null && !SelectedCard.IsShipped);

    /// <summary>删除选中的人格卡（二次确认；实际是移动到本程序的 deleted-personas 备份目录）。</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var card = SelectedCard;
        if (card is null || card.IsShipped) return;

        // 删除不可从界面恢复，必须让用户明确确认
        var window = Application.Current?.MainWindow;
        var message = $"确定删除人格卡「{card.DisplayName}」吗？\n\n" +
                      "（不会真删：会移动备份到本程序的 deleted-personas 目录，可手工找回）";
        var confirm = window is null
            ? MessageBox.Show(message, "删除人格卡", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(window, message, "删除人格卡", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (!_service.Delete(card, out var error))
        {
            StatusText = "删除失败：" + error;
            return;
        }

        Reload(keepId: null);
        StatusText = $"已删除人格卡「{card.DisplayName}」（已备份到 deleted-personas）。";
    }

    private bool CanDelete() => SelectedCard is not null && !SelectedCard.IsShipped;

    /// <summary>
    /// 把选中的人格卡设为新会话的默认人格（写 settings.yaml 的 agent-presets.default）。
    /// 这是 dsh 唯一认可的「选中人格」入口；随附预设同样可以设为默认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetDefault()
    {
        var card = SelectedCard;
        if (card is null) return;

        if (!_service.SetDefault(card.Id, out var error))
        {
            StatusText = "设为默认失败：" + error;
            return;
        }

        Reload(keepId: card.Id);
        StatusText = "已设为新会话默认人格";
    }

    /// <summary>把选中卡（含随附只读卡）复制成一张可编辑的「我的卡」。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SaveAsCopy()
    {
        var card = SelectedCard;
        if (card is null) return;

        var created = _service.Create(
            card.Id,
            PersonaService.SuggestId(card.Id) + "-copy",
            card.DisplayName + " 副本",
            card.Description,
            card.SystemPrompt,
            out var error);
        if (created is null)
        {
            StatusText = "另存为失败：" + error;
            return;
        }

        Reload(keepId: created.Id);
        StatusText = error.Length > 0
            ? error
            : $"已另存为我的卡「{created.DisplayName}」（id：{created.Id}），现在可以编辑了。";
    }

    /// <summary>在资源管理器里定位选中卡的目录（未选中时打开用户卡根目录）。</summary>
    [RelayCommand]
    private void OpenDirectory() => PersonaService.OpenInExplorer(SelectedCard?.DirectoryPath ?? _service.UserPresetsRoot);

    /// <summary>重新扫描人格卡目录与默认人格设置。</summary>
    [RelayCommand]
    private void Refresh()
    {
        Reload(keepId: IsNewMode ? null : SelectedCard?.Id);
        StatusText = "已刷新人格卡列表。";
    }

    private bool HasSelection() => SelectedCard is not null;

    /// <summary>选中变化后，通知所有依赖选中的命令重新评估可用性。</summary>
    private void NotifySelectionCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        SetDefaultCommand.NotifyCanExecuteChanged();
        SaveAsCopyCommand.NotifyCanExecuteChanged();
        OpenDirectoryCommand.NotifyCanExecuteChanged();
    }

    // ---- 数据刷新 ----

    /// <summary>全量重载卡片列表，并按 id 尽量恢复选中；同时刷新默认人格摘要与环境提示。</summary>
    /// <param name="keepId">要恢复选中的人格卡 id；为 null 则选中列表首项。</param>
    private void Reload(string? keepId)
    {
        var cards = _service.LoadAll();

        Cards.Clear();
        foreach (var card in cards) Cards.Add(card);

        // 基底候选与卡片列表同源（随附预设正好是官方推荐的复制来源）
        BaseTemplates.Clear();
        foreach (var card in cards) BaseTemplates.Add(card);

        var defaultId = _service.ReadDefaultPresetId();
        DefaultSummary = BuildDefaultSummary(cards, defaultId);
        TemplateHint = _service.HasShippedTemplates
            ? string.Empty
            : "未找到 dsh 自带的预设模板（dsh-agent-presets）；此时新建只会生成一个最小骨架，工具链不完整。";

        SelectedCard = cards.FirstOrDefault(c => c.Id == keepId) ?? cards.FirstOrDefault();
        SelectedBaseTemplate = BaseTemplates.FirstOrDefault(c => c.Id == PreferredBaseId) ?? BaseTemplates.FirstOrDefault();
    }

    /// <summary>拼当前默认人格摘要：区分「未设置」「已设置且本机存在」「已设置但本机找不到」。</summary>
    private static string BuildDefaultSummary(IReadOnlyList<PersonaCard> cards, string defaultId)
    {
        if (string.IsNullOrWhiteSpace(defaultId)) return "当前新会话默认人格：未设置（dsh 使用自带默认）";

        var card = cards.FirstOrDefault(c => string.Equals(c.Id, defaultId, StringComparison.OrdinalIgnoreCase));
        return card is not null
            ? $"当前新会话默认人格：{card.DisplayName}（{card.Id}）"
            : $"当前新会话默认人格：{defaultId}（本机未找到这张卡）";
    }
}
