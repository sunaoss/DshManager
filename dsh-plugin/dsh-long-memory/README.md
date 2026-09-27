# dsh-long-memory

大容量长期记忆插件（DeepSeek Harness / dsh）。它把一份用户自定义的记忆文件，
作为**独立于人格卡**的长期背景知识，注入到每个会话中。

## 它做什么

- 监听 `agent/pre-step`，在每轮对话的**第 1 步**读取记忆文件并注入一则 user 消息
  （`<system-reminder>` 包裹，`source.kind = 'long-memory'`）。
- 注入预算独立于人格卡 / 工作区指令的 64KB，默认 **256KB**。
- 只注入一次/轮，append-only，对 KV Cache 友好。
- 文件不存在、内容为空、总开关关闭或纯文件超过读取上限时**不注入**。
- 任何异常都不会影响 dsh 主流程。

## 记忆文件放哪

默认路径：`<DSH_HOME>/MEMORY.md`（例如 `C:\Users\<你>\.dsh\MEMORY.md`）。

直接编辑该文件即可，无需重启 dsh —— 插件按 `mtime + size` 判断是否重读。

## 如何配置

在 `<DSH_HOME>/settings.yaml` 中追加 `long-memory` 段（管理面板亦可通过
settings 命名空间写入）：

```yaml
long-memory:
  enabled: true
  file: C:\Users\<你>\.dsh\MEMORY.md
  maxBytes: 524288          # 注入正文上限（字节，默认 262144）
  maxSourceBytes: 2097152   # 单文件读取上限（默认 2097152）
```

也可在挂载处（cordis patch 的 `config`）配置同样的字段，作为 `base` 层；
解析顺序为 **schema 默认 → base → 用户 settings.yaml**。

## 卸载

```powershell
node "<DSH_HOME>\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js" plugin --profile web remove dsh-long-memory
```

或移除 profile 的 `cordis.patch.yml` 中对应的 `long-memory` insert 条目，
并删除 `profiles\web\plugins\dsh-long-memory\`（若为 patch 方式挂载）。

## 排查

日志：`<DSH_HOME>/dsh-long-memory.log`
（记录插件加载、settings 注册、每次注入的 turn/step/字节数/文件路径、异常）。
