# 鲸鱼娘 · DeepSeek Harness 管理面板

> **本项目由 al（留恋风尘四溅）自主创作。**

一个面向 [DeepSeek Harness](https://github.com/deepseek-ai) 的 Windows 桌面管理面板：把原本要在命令行里敲的
安装、启停、配置、插件、记忆库、人格等操作，收进一个图形界面里。

- 技术栈：.NET 8 + WPF（MVVM，CommunityToolkit.Mvvm）
- 打包：Inno Setup 6，自包含发布（用户机器无需预装 .NET 运行时）
- 支持系统：Windows 10 / 11 x64

---

## 功能

| 页面 | 能做什么 |
| --- | --- |
| **首页** | 一键启动 / 停止 dsh 服务、看运行状态与端口、实时日志、打开网页面板、终止外部实例 |
| **配置** | 以表单方式编辑 `~/.dsh/settings.yaml`（保注释、行级最小改写），改前自动备份 |
| **插件** | 浏览插件市场（下载量 / 版本 / 描述）、一键安装卸载、查看已装列表 |
| **记忆库** | 编辑长期记忆 `AGENTS.md`、按项目目录隔离作用域、自动备份与回滚 |
| **人格** | 人格模板的创建、编辑、预览、删除（删除走可恢复的回收目录） |
| **文件** | 快速浏览 dsh 数据目录（限深、只读、防误删） |
| **聊天** | 与鲸鱼娘人设的本地关键词对话，带表情包 |
| **日志** | 面板自身运行日志 |

另外内置两个 dsh 扩展插件源码，见 `dsh-plugin/`：

- `dsh-long-memory` —— 长期记忆插件，面板「一键安装」时从本地复制到 profile，不依赖联网。
- `dsh-memory-probe` —— 关键假设验证探针（能否收到 `agent/pre-step` 事件），仅用于开发排查。

---

## 安装

### 方式一：安装包（推荐）

下载 `DshManager-Setup-1.9.7.exe`（见本仓库 `installer/output/`）后双击安装。

- 默认安装到 `C:\Program Files\DshManager`，**需要管理员权限**
- 安装完成后可选「立即启动」
- 卸载：开始菜单 →「卸载 鲸鱼娘 · DeepSeek Harness 管理面板」

静默安装：

```powershell
DshManager-Setup-1.9.7.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### 方式二：从源码构建

前置要求：

- .NET SDK 8.0 或更高（`dotnet --list-sdks` 能看到 `8.0.x`）
- [Inno Setup 6](https://jrsoftware.org/isdl.php)（仅打包安装包时需要）

```powershell
# 只构建 + 跑测试
dotnet build DshManager.sln -c Release
dotnet test  DshManager.Tests/DshManager.Tests.csproj -c Release

# 一键发布 + 打包（发布自包含产物到 publish\，再编译成安装包）
pwsh -File tools/build-installer.ps1
```

> **注意**：如果你的 Windows 用户名里含**单引号**，或者仓库放在含单引号的路径下，
> `dotnet publish -o <该路径>` 会触发 `MSB3094` 错误（MSBuild 的发布项变换被解析坏）。
> `tools/build-installer.ps1` 已经内置规避方案：先发布到路径干净的中转目录（默认
> `C:\dshpub`），再整体拷回仓库 `publish\`。详见脚本头部说明。

---

## 项目结构

```
DshManager/            WPF 主程序（Services / ViewModels / Views / Models）
DshManager.Tests/      xUnit 测试（519 个用例）
dsh-plugin/            随程序分发的 dsh 扩展插件源码
  dsh-long-memory/       长期记忆插件
  dsh-memory-probe/      开发用验证探针
installer/
  DshManager.iss         Inno Setup 打包脚本
  output/                打包产物（安装包 exe）
tools/
  build-installer.ps1    一键发布 + 打包
  verify-install.ps1     安装/卸载全流程验收脚本（需管理员）
publish/               发布输出（构建产物，不入版本库）
```

---

## 用户数据在哪

面板自身的数据与 dsh 的数据是分开的：

| 内容 | 位置 |
| --- | --- |
| 面板设置 / 记忆库 / 人格 | `%LOCALAPPDATA%\DshManager\Data` |
| 内置 Node 运行时 | `%LOCALAPPDATA%\DshManager\runtimes` |
| 表情包、日志 | `%LOCALAPPDATA%\DshManager\Stickers`、`\logs` |
| dsh 本体配置与数据 | `%USERPROFILE%\.dsh` |

卸载会清理 `%LOCALAPPDATA%\DshManager` 下的 Data / Stickers / runtimes / logs。
注意：机器级安装 + 每用户数据的组合下，若同一台机器有多个用户都用过面板，
其他用户目录下的数据不会被清理（编译时会出现一条 `UsedUserAreasWarning`，属预期内）。

---

## 已知限制

1. **桌面快捷方式默认不创建。** `[Tasks]` 里的 `desktopicon` 标记为 `Flags: unchecked`，
   静默安装传 `/MERGETASKS=desktopicon` 目前**不会**生效（实测安装器返回 0 但桌面无图标）。
   需要桌面图标的话，从开始菜单把快捷方式拖到桌面即可。
2. 安装需要管理员权限（只支持机器级安装，避免「机器级 / 用户级」两套安装互相不认识）。
3. 简体中文安装向导需要 Inno Setup 的 `ChineseSimplified.isl`。官方安装包**不附带**该文件，
   缺失时会自动回退英文向导（业务文案仍是中文）。获取方式见 `installer/DshManager.iss` 注释。
4. 不支持 32 位系统。

---

## 版本历史

### 1.9.7

经一轮完整的源码审计后集中修复，共 43 项，主要几类：

- **资源泄漏**：`HomeView` 缺少 `Unloaded` 导致 `HomeViewModel` 及其 `DispatcherTimer` 永不回收、
  静态单例订阅随导航线性增长；进程句柄未释放。
- **崩溃防护**：补上 `DispatcherUnhandledException` / `TaskScheduler.UnobservedTaskException`
  全局处理器与单实例 Mutex；`Process.Start` 调用点加异常兜底。
- **并发与原子写**：`JsonStoreService` 改为「临时文件 + 原子替换 + 加锁」，新增 `Update<T>` 把
  「读-改-写」放进同一把锁，消除多个写入方互相覆盖；`settings.yaml` 外部改动检测。
- **命令与参数注入**：包名/版本白名单校验（`PackageSpecValidator`）、去掉 `cmd mklink` 拼接、
  命令与日志路径统一走 `QuoteForCmd`，递归枚举跳过 reparse point。
- **打包与版本**：补齐 EXE 与安装包的版本资源（不再自报 1.0.0.0）、排除 pdb 与 12 个多余
  本地化目录、卸载时清理用户数据、按 PID 而非映像名结束进程、新增可复现的一键打包脚本。

同时修复了此前未暴露的 `YamlTextEditor` 缩进步长硬编码问题（父级用 4 空格时新键会挂错层级）。

测试用例从 453 增加到 519。

---

## 许可

本项目由 al（留恋风尘四溅）自主创作。仓库暂未附带开源许可证文件；
在补充 LICENSE 之前，默认保留所有权利。如需在其它项目中复用，请先联系作者。
