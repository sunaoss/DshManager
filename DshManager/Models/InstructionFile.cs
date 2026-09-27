namespace DshManager.Models;

/// <summary>指令链中的一个文件（记忆文件）。</summary>
public class InstructionFile
{
    public string Path { get; init; } = string.Empty;        // 绝对路径
    public string FileName { get; init; } = string.Empty;    // AGENTS.md / CLAUDE.md / AGENTS.local.md / CLAUDE.local.md
    public string Directory { get; init; } = string.Empty;   // 所在目录
    public int Depth { get; init; }                          // 相对项目根的层级深度（0 = 项目根）
    public bool Exists { get; init; }
    public long SizeBytes { get; init; }
    public bool IsLocalOverlay { get; init; }                // 是否为 AGENTS.local.md / CLAUDE.local.md
    public string ScopeText { get; init; } = string.Empty;   // 中文层级描述：「项目根」「工作目录」「上级目录（第 N 层）」等
    public string SizeText { get; init; } = string.Empty;    // 「4.0 KB」；文件不存在时为「—」
    public string PriorityText { get; init; } = string.Empty;// 优先级说明：「低（较宽泛）」→「高（最具体）」
}

/// <summary>一次指令链发现的完整结果。</summary>
public class InstructionChain
{
    public string WorkDirectory { get; init; } = string.Empty;
    public string ProjectRoot { get; init; } = string.Empty;
    public bool ProjectRootFound { get; init; }              // 是否真的找到了 .git 标记
    public IReadOnlyList<InstructionFile> Files { get; init; } = [];  // 从宽泛到具体排序，仅含存在的文件
    public long TotalBytes { get; init; }                    // Files 中已存在文件的字节合计
}

/// <summary>记忆预算配置。</summary>
public class DshBudgetConfig
{
    public int MaxBytes { get; init; } = 65536;
    public int MaxSourceBytes { get; init; } = 1048576;
    public bool IsDefault { get; init; }                     // 是否用的默认值（配置里未显式设置）
    public string SourceDescription { get; init; } = string.Empty;  // 如「人格卡 greedy-whale-girl 的 agent.cordis.yml」或「官方默认值」
}
