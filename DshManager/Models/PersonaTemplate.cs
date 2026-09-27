namespace DshManager.Models;

/// <summary>
/// 人格模板，持久化到 Data/personas.json（通常以列表形式存储）。
/// </summary>
public class PersonaTemplate
{
    /// <summary>模板名称（唯一标识用途）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>角色描述 / System Prompt，注入给模型的人格设定。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>模板的补充说明文字。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>模板创建时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; }
}
