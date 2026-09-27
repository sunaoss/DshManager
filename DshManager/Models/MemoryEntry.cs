namespace DshManager.Models;

/// <summary>
/// 记忆条目，持久化到 Data/memory.json（通常以列表形式存储）。
/// </summary>
public class MemoryEntry
{
    /// <summary>关联的 Agent 或项目名称。</summary>
    public string AgentOrProject { get; set; } = string.Empty;

    /// <summary>记忆内容正文。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>条目创建时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>条目最近更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; }
}
