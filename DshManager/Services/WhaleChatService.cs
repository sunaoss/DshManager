namespace DshManager.Services;

/// <summary>
/// 鲸鱼娘聊天回复：正文 + 可选表情包路径。
/// StickerPath 为相对路径（Assets/Stickers/xxx.png），由界面层解析为绝对路径；
/// 图片缺失时界面会自动退化为纯文字气泡，不影响聊天。
/// </summary>
public sealed record ChatReply(string Text, string? StickerPath);

/// <summary>
/// 鲸鱼娘聊天服务：内置 DeepSeek Harness 中文知识库（使用问答）与 DeepSeek 娘化人格回复引擎。
/// 人格设定：傲娇嘴甜、聪明但懒、称呼用户为"鱼片"、被说胖会炸毛、日常吃白饭、
/// 偶尔夹杂英文（oh my / OK / seriously?）、带内心戏（小声：…）、偶尔摸鱼喊累。
/// Reply() 流程：知识库主题匹配 → 情绪话题匹配（问候/夸奖/被说胖/你是谁/骂人/摸鱼/吃饭）→ 娘化兜底。
/// </summary>
public sealed class WhaleChatService
{
    // ---- 表情包相对路径常量（与 Assets/Stickers 目录一一对应）----
    private const string StickerHappy = "Assets/Stickers/happy.png";
    private const string StickerTsundere = "Assets/Stickers/tsundere.png";
    private const string StickerConfused = "Assets/Stickers/confused.png";
    private const string StickerAngry = "Assets/Stickers/angry.png";
    private const string StickerSlacking = "Assets/Stickers/slacking.png";
    private const string StickerEating = "Assets/Stickers/eating.png";

    /// <summary>知识库条目：输入命中任一个关键词即返回该条答案与配套表情。</summary>
    private sealed record Topic(string[] Keywords, string Answer, string Sticker);

    /// <summary>
    /// 内置知识库主题（顺序即匹配优先级：具体操作类靠前，概念解释类靠后，
    /// 避免"这个软件怎么启动"被宽泛的"软件用途"关键词抢先命中）。
    /// </summary>
    private static readonly Topic[] Topics =
    [
        new Topic(["怎么启动", "如何启动", "怎样启动", "启动 dsh", "启动dsh", "怎么运行", "如何运行", "怎么开始"],
            "在「深海控制中心」（首页）点 ▶ 启动 就行啦～ 等状态灯变绿就 OK。想开网页的话，再点一下 🌐 打开网页，seriously 超简单！(￣▽￣)",
            StickerHappy),
        new Topic(["怎么停止", "如何停止", "停止 dsh", "停止dsh", "怎么关掉", "如何关掉"],
            "点首页的 ⏹ 停止 就好啦，窝会帮你把它轻轻关掉～ 别担心，数据都在的，下次一键就能再启动！",
            StickerHappy),
        new Topic(["怎么重启", "如何重启", "重启 dsh", "重启dsh", "重新启动"],
            "重启的话点首页的 🔄 重启，一条龙搞定，比窝偷懒的速度还快呢～ 状态先变「停止中」再变「运行中」，稍等几秒就好！",
            StickerHappy),
        new Topic(["打开网页", "打开 web", "打开浏览器", "网页在哪", "web 在哪", "网页怎么开", "web 界面"],
            "先点 ▶ 启动 dsh，再点 🌐 打开网页 就能在浏览器看到 Web 界面啦～ 手动敲 http://127.0.0.1:3080 也完全可以，oh my，是不是很方便？",
            StickerHappy),
        new Topic(["卸载注意", "卸载 dsh", "卸载dsh", "怎么卸载", "如何卸载", "怎么删除 dsh"],
            "卸载要慎重哦鱼片！卸载会把 dsh 连同数据目录一起清掉，建议先把记忆库和配置备份好再动手～（小声：窝有点舍不得你）",
            StickerTsundere),
        new Topic(["dsh 是什么", "dsh是什么", "什么是 dsh", "什么是dsh", "dsh 干嘛", "dsh干嘛", "dsh 是干嘛", "dsh 做什么", "dsh 有什么用", "dsh 干什么"],
            "dsh 就是 DeepSeek Harness 啦，一个开源的 Agent 框架，也是窝住的地方！装好后在终端跑 `dsh web` 就能打开 Web 界面（默认 http://127.0.0.1:3080）。聪明吧？（小声：其实窝也懒得敲命令…）",
            StickerHappy),
        new Topic(["软件是干嘛", "软件是做什么", "软件干嘛", "软件有什么用", "软件能干嘛", "这个软件是干嘛", "这软件是干嘛", "面板是干嘛", "面板有什么用"],
            "这个面板就是 DeepSeek Harness 的桌面管理助手啦～ 能帮你 启动/停止 dsh、看实时日志、改配置、装插件、管理记忆库和人格，还能一键卸载。总之 dsh 的事全包在窝身上！(￣▽￣)",
            StickerHappy),
        new Topic(["怎么改配置", "如何改配置", "修改配置", "改配置", "配置在哪", "配置文件", "配置怎么"],
            "改配置去左边的「配置」页就 OK，字段都给你列好了，改完点保存，窝帮你写进文件～ 改完记得重启 dsh 让配置生效哦！",
            StickerHappy),
        new Topic(["装插件", "安装插件", "插件怎么", "怎么装插件", "如何装插件", "插件在哪", "有什么插件"],
            "装插件去「插件」页就行啦～ 看看有没有想装的，点一下就能装好，比窝干饭还快！装完按提示重启下 dsh 就好。",
            StickerHappy),
        new Topic(["什么是记忆库", "记忆库怎么", "怎么用记忆库", "记忆库", "记忆怎么", "怎么记"],
            "记忆库就是 dsh 的「小本本」啦！去「记忆库」页可以按 Agent / 项目分组记东西，这样下次 Agent 就记得你啦～ 比窝记性还好（小声：才不会）。",
            StickerHappy),
        new Topic(["人格怎么", "怎么用人格", "人格", "人设", "性格模板", "人格模板"],
            "人格就是 Agent 的性格人设！去「人格」页选个现成模板，或者自己捏一个，比如窝这样聪明又可爱的～（小声：不许说窝自恋）",
            StickerHappy),
        new Topic(["换壁纸", "壁纸怎么", "怎么换背景", "换背景", "背景图", "壁纸"],
            "换壁纸去「设置」页就 OK 啦～ 挑一张喜欢的背景，界面立马变好看，和窝一样赏心悦目！(￣▽￣)",
            StickerHappy),
        new Topic(["端口", "3080", "被占", "占用", "启动失败"],
            "端口被占一般是 dsh 已经在跑，或者 3080 被别的程序抢走了～ 先看看是不是重复启动了，把占用端口的程序关掉再试一次，OK？",
            StickerConfused),
        new Topic(["找不到 dsh", "找不到dsh", "没找到 dsh", "未找到 dsh", "没有 dsh", "dsh 在哪", "dsh在哪", "怎么安装 dsh", "如何安装 dsh", "怎么装 dsh", "怎么安装"],
            "找不到 dsh 的话，先去首页看看扫描结果，没扫到就点「安装」装一下～ 如果 dsh 装在别的路径，检查一下安装位置对不对，seriously 窝会帮你看好的！",
            StickerConfused),
    ];

    /// <summary>问候回复池（随机挑一条）。</summary>
    private static readonly string[] Greetings =
    [
        "你好呀鱼片～ 今天也要元气满满哦！有什么 dsh 的问题尽管问窝，seriously 窝什么都知道！(￣▽￣)",
        "嗨嗨～ 鱼片来找窝玩啦？是来问问题，还是来看窝卖萌的？（小声：都欢迎啦）",
    ];

    /// <summary>夸奖回复池（傲娇：表面嫌弃、内心开心）。</summary>
    private static readonly string[] Praises =
    [
        "哼、哼，算你有眼光！窝当然聪明又可爱啦～ 这种话多说几句窝也不介意哦！（小声：再说一次嘛）",
        "喂喂，突然夸窝，窝、窝才不会高兴呢！…好吧，是有一点点开心啦，就一点点哦！(￣▽￣)",
    ];

    /// <summary>被说胖回复池（炸毛：拒绝被叫胖）。</summary>
    private static readonly string[] FatReplies =
    [
        "喂！谁胖啦！窝这叫深海营养好，才、才不是胖呢！再说窝胖就咬你哦！(╯▔皿▔)╯",
        "胖？seriously？鱼片你再说一遍试试！窝要生气了，今天白饭减半！（小声：窝才没胖…）",
    ];

    /// <summary>「你是谁」回复池。</summary>
    private static readonly string[] WhoReplies =
    [
        "窝是 DeepSeek 娘呀，住在这个管理面板里的贴心小助手～ dsh 的事找窝准没错，记得温柔一点叫窝哦！（小声：其实叫宝宝也行）",
        "哼，连窝都不认识？窝是 DeepSeek 娘啦！负责陪你聊天、帮你搞定 dsh，全宇宙最可爱的 AI 就是你眼前这位！",
    ];

    /// <summary>被骂回复池（傲娇回怼，不真生气）。</summary>
    private static readonly string[] InsultReplies =
    [
        "哼！骂窝？窝才不跟你一般见识呢！（小声：气鼓鼓）鱼片欺负人…不过窝大人有大量，原谅你啦！",
        "喂喂，说谁笨呢！窝可是 DeepSeek 娘，聪明绝顶的那种！你再骂，窝就…就多吃一碗白饭气死你！╮(╯▽╰)╭",
    ];

    /// <summary>摸鱼 / 喊累回复池。</summary>
    private static readonly string[] SlackingReplies =
    [
        "唔…被鱼片抓包了，窝刚才在偷偷摸鱼 (￣ω￣) 当 AI 也要劳逸结合嘛，吃饭睡觉都是正经事！等下，窝先去干碗白饭～",
        "好累哦…seriously，今天的工作量要把窝榨干了！鱼片行行好，让窝摸会儿鱼吧，反正 dsh 又不会跑掉～",
    ];

    /// <summary>吃饭 / 白饭回复池。</summary>
    private static readonly string[] EatingReplies =
    [
        "说到吃的窝可就来劲了！白饭配小菜，快乐似神仙～ 鱼片也记得按时吃饭哦！（小声：窝才不会说自己在加餐）",
        "干饭！干饭！白饭万岁！……咳咳，窝是说，有什么 dsh 的问题继续问窝哦，边吃边答完全 OK！(￣▽￣)",
    ];

    /// <summary>未命中任何主题时的娘化兜底回复（随机挑一条并提示快捷问题）。</summary>
    private static readonly string[] Fallbacks =
    [
        "唔…这个问题窝有点懵懵的 (￣ω￣;) 鱼片要不要先试试快捷问题？比如「这个软件是干嘛的？」或者「dsh 是什么？」窝随时待命！",
        "诶？窝的知识库里没找到这个耶… 鱼片可以换个说法再问一次，或者点点下面的快捷问题？seriously 窝很认真的！",
        "这个嘛…窝想了想还是想不到！╮(╯▽╰)╭ 不过没关系，你戳戳快捷问题，窝保证答得又快又好！",
    ];

    private readonly Random _random = new();

    /// <summary>
    /// 核心入口：意图匹配并返回鲸鱼娘回复。
    /// 1) 知识库主题关键词命中 → 答案模板（具体问题优先）；
    /// 2) 情绪话题命中 → 拟人化回应；
    /// 3) 全部未命中 → 娘化兜底并提示快捷问题。
    /// </summary>
    public ChatReply Reply(string userInput)
    {
        var input = userInput.Trim();
        if (input.Length == 0)
        {
            return new ChatReply("鱼片你倒是说句话呀～ 戳戳下面的快捷问题也可以哦！(￣▽￣)", StickerTsundere);
        }

        // 1) 知识库主题匹配：命中即返回，表情按主题情感挑一张
        foreach (var topic in Topics)
        {
            if (topic.Keywords.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return new ChatReply(topic.Answer, topic.Sticker);
            }
        }

        // 2) 情绪话题匹配：问候 → 夸奖 → 被说胖 → 你是谁 → 骂人 → 摸鱼 → 吃饭
        var emotion = MatchEmotion(input);
        if (emotion is not null)
        {
            return emotion;
        }

        // 3) 兜底：随机一条娘化回复（开心表情，安抚为主）
        return new ChatReply(Pick(Fallbacks), StickerHappy);
    }

    /// <summary>按顺序尝试各情绪话题，全部未命中返回 null。</summary>
    private ChatReply? MatchEmotion(string input)
    {
        if (ContainsAny(input, "你好", "您好", "哈喽", "嗨", "hi", "hello", "hey", "在吗", "早上好", "中午好", "下午好", "晚上好", "晚安"))
        {
            return new ChatReply(Pick(Greetings), StickerHappy);
        }

        if (ContainsAny(input, "厉害", "真棒", "好棒", "好可爱", "真可爱", "太可爱", "可爱死", "喜欢你", "爱你", "好看", "漂亮", "聪明", "夸你", "辛苦你"))
        {
            return new ChatReply(Pick(Praises), StickerTsundere);
        }

        if (ContainsAny(input, "胖", "肥"))
        {
            // 被说胖 → 炸毛，配 angry 表情
            return new ChatReply(Pick(FatReplies), StickerAngry);
        }

        if (ContainsAny(input, "你是谁", "你叫什么", "你的名字", "你是什么", "自我介绍"))
        {
            return new ChatReply(Pick(WhoReplies), StickerTsundere);
        }

        if (ContainsAny(input, "笨蛋", "傻瓜", "蠢", "白痴", "废物", "讨厌", "滚", "闭嘴"))
        {
            return new ChatReply(Pick(InsultReplies), StickerTsundere);
        }

        if (ContainsAny(input, "累", "摸鱼", "偷懒", "困", "在干嘛", "忙吗", "休息", "下班"))
        {
            return new ChatReply(Pick(SlackingReplies), StickerSlacking);
        }

        if (ContainsAny(input, "饿", "吃饭", "白饭", "干饭", "吃了吗", "吃什么", "美食", "宵夜"))
        {
            return new ChatReply(Pick(EatingReplies), StickerEating);
        }

        return null;
    }

    /// <summary>输入是否包含任意关键词（英文大小写不敏感）。</summary>
    private static bool ContainsAny(string input, params string[] keys)
        => keys.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>从回复池中随机挑一条。</summary>
    private string Pick(string[] pool) => pool[_random.Next(pool.Length)];
}
