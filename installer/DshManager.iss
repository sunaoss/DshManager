; ============================================================================
;  DeepSeek Harness 管理面板（鲸鱼娘）—— Inno Setup 安装脚本
;
;  【版本号同步规则】本文件的 AppVersion 必须与 DshManager\DshManager.csproj 里的
;  <Version> 完全一致（唯一真源在 csproj，本文件是它的副本）。改一处必须改另一处，
;  否则会出现「安装包叫 1.9.7、程序属性页显示别的版本」这种两头对不上的情况。
;  同步步骤：
;    1) 改 DshManager\DshManager.csproj 的 <Version> / <FileVersion> / <AssemblyVersion>
;       （AssemblyVersion 用四段格式 1.9.7.0）；
;    2) 改本文件下面的 AppVersion（三段）与 AppVersionQuad（四段，必须同一个版本）；
;    3) 跑 tools\build-installer.ps1 重新发布 + 打包（该脚本会在打包前比对两处版本，
;       不一致就直接报错退出，不会悄悄打出错版本的包）。
;  为什么不自动从 csproj 读：ISPP 没有 XML 解析能力，只能做脆弱的字符串解析；而「读
;  已编译的 publish\DshManager.exe 版本号」又要求 publish 必须先跑、且不能是旧产物，
;  反而更容易打出版本漂移的包。这里选择「显式声明 + 注释约束 + 脚本校验」。
;
;  编译方式（需先执行 dotnet publish，产物位于 ..\publish）：
;    & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DshManager.iss
;  产物：installer\output\DshManager-Setup-<版本>.exe
;  一键流程（发布 + 打包）：pwsh -File tools\build-installer.ps1
; ============================================================================

#define AppName "鲸鱼娘 · DeepSeek Harness 管理面板"
#define AppShortName "DshManager"
; ↓ 必须与 DshManager\DshManager.csproj 的 <Version> 一致（三段格式，用于 AppVersion 与安装包文件名）
#define AppVersion "1.9.7"
; ↓ 与上面的 AppVersion 是同一个版本，只是补成四段数字。
;    Inno 的 VersionInfoVersion / VersionInfoProductVersion 只接受四段数字（不接受 "1.9.7"），
;    所以单独定义一个四段常量，避免在 [Setup] 里手写第二份版本号。
#define AppVersionQuad "1.9.7.0"
#define AppPublisher "al（留恋风尘四溅）"
#define AppExeName "DshManager.exe"

[Setup]
AppId={{8F3C2A64-9E17-4B52-9C1D-2A6E7F4B0D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}

; 【安装位置与权限：只保留机器级安装】
; 原脚本允许 PrivilegesRequiredOverridesAllowed=commandline，于是可以用 /CURRENTUSER
; 装到 %LOCALAPPDATA%\Programs\DshManager 并把卸载键写进 HKCU。问题在于 AppId 是同一个：
; 机器级与用户级成了两套互不知道的安装 —— 「应用和功能」里出现两条、开始菜单重复、
; 卸掉一套另一套还在。
; 为什么不去做「两套并存但互不混淆」：Inno 的 AppId 是编译期静态值，没法按安装模式
; 切换成两个不同的 AppId；要真正区分只能编译两个 AppId 不同的安装包（另立 .iss）。
; 判断：与其引入一套需要运行期验证的复杂逻辑，不如直接去掉这条命令行旁路 ——
; 一次只可能存在一套安装，歧义彻底消失，且默认安装行为（要求管理员）完全不变。
; 取舍：没有管理员权限的用户不能再安装。若将来确实需要免管理员安装，请复制本脚本为
; DshManager-user.iss，使用不同的 AppId 与 DefaultDirName={localappdata}\Programs\{#AppShortName}。
PrivilegesRequired=admin

; 仅支持 64 位系统（发布产物为 win-x64）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 安装包输出位置与文件名
OutputDir=output
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; 【文件属性里的版本资源】不写这些，生成的安装包 VersionInfo 全是 0.0.0.0（实测
; FileVersionRaw=0.0.0.0、ProductVersionRaw=0.0.0.0、FileVersion 是空串），
; SmartScreen 与企业软件清单工具读不到版本号。这里补齐四段版本 + 产品信息。
VersionInfoVersion={#AppVersionQuad}
VersionInfoProductVersion={#AppVersionQuad}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoCopyright=Copyright (C) {#AppPublisher}

; 若面板正在运行，安装前自动提示关闭，避免文件占用
CloseApplications=yes
RestartApplications=no

; 卸载入口显示图标
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; 不需要「选择开始菜单文件夹」这一页
DisableProgramGroupPage=yes

; 【语言选择框】不弹。这是纯中文产品，让用户在中文/英文向导之间做选择没有意义；
; 只有在 [Languages] 里成功启用中文时才会有两种语言，此时按系统语言自动挑选即可
; （非中文系统自动回落到英文）。语言文件缺失时只有英文一种语言，本来也不会弹框。
ShowLanguageDialog=no

[Languages]
; 【关于简体中文】官方翻译文件 ChineseSimplified.isl 并没有随 Inno Setup 6 的官方安装包
; 分发：本机实测 C:\Program Files (x86)\Inno Setup 6\Languages\ 下共 29 个语言文件，
; 不含中文。来源是官方 Translations 页面：
;     https://jrsoftware.org/files/istrans/   → 下载 ChineseSimplified.isl
; 把它放到 <Inno Setup 6 安装目录>\Languages\ChineseSimplified.isl 后，下面的条件判断
; 会自动启用中文向导（无需改本脚本）。
; 为什么要写成条件判断：直接写死 MessagesFile: "compiler:Languages\ChineseSimplified.isl"
; 会在缺文件的机器上直接编译失败。现在缺失只会回退英文并在编译日志里提示。
; 英文条目始终保留作为回退语言。
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#pragma message "已找到 ChineseSimplified.isl：安装向导使用简体中文"
#else
#pragma message "未找到 ChineseSimplified.isl：安装向导回退英文（[Tasks]/[Run] 中的中文业务文案不受影响）"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
; 打包整个发布目录（自包含 .NET 运行时 + 程序 + 表情包资源）
;
; 【Excludes 的两条理由】
;  1) *.pdb —— publish\DshManager.pdb（约 211 KiB）是调试符号，终端用户用不到，
;     打进去只是白白增大安装包（发布脚本里也没有生成它的必要）。
;  2) 12 个框架本地化目录 —— cs\ de\ es\ fr\ it\ ja\ ko\ pl\ pt-BR\ ru\ tr\ zh-Hant\
;     每个目录 17 个 *.resources.dll（合计约 13.8 MiB）。它们是 WPF/WinForms 框架自己的
;     附属资源程序集：只影响框架抛出的异常消息、系统对话框文案的语言，而本应用界面
;     全部是自绘中文、不会跟着这些资源切换语言（代码里没有任何 CurrentUICulture 相关逻辑）。
;     判断：保留 zh-Hans（约 1.1 MiB）让中文系统上的框架异常消息仍是中文，其余 12 个
;     语言目录全部排除 —— 这是 13.8 MiB 的净收益，且不损失中文体验。
;     更根本的做法是让 publish 干脆不生成它们，见 DshManager\DshManager.csproj 里的
;     <SatelliteResourceLanguages>；这里仍然保留 Excludes 作为双保险，因为 publish\ 目录
;     有可能是加该属性之前构建出来的旧产物（本次就是这样）。
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb,cs\*,de\*,es\*,fr\*,it\*,ja\*,ko\*,pl\*,pt-BR\*,ru\*,tr\*,zh-Hant\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单快捷方式（含卸载入口由 Inno 自动创建）
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后可选立即启动面板。
; 【为什么必须加 runasoriginaluser】管理员模式安装时，若直接以当前（已提权）身份启动面板，
; 面板进程会带着管理员令牌去写 %LOCALAPPDATA%\DshManager\*，生成的目录/文件属主是
; Administrators —— 之后普通用户运行面板反而写不进去（设置存不了、表情存不了）。
; 加了 runasoriginaluser 后，即使安装本身是提权的，启动的也是「最初登录的那个普通用户」
; 身份。原有的 nowait / postinstall / skipifsilent 全部保留（静默安装时该标志会被忽略）。
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; 【关于编译时的 UsedUserAreasWarning】
; 本脚本 PrivilegesRequired=admin，却在下面以及 [Code] 段里用了 {localappdata}（每用户目录），
; ISCC 因此会打印一条警示："The [Setup] section directive \"PrivilegesRequired\" is set to
; \"admin\" but per-user areas (localappdata) are used by the script." —— 这是预期内的，不是错误。
; 原因与已知限制：面板自己把数据放在每用户目录（%LOCALAPPDATA%\DshManager），而安装是机器级的，
; 因此卸载时 {localappdata} 解析到的是「执行卸载的那个用户」的目录。若同一台机器上另一个
; 标准用户也用过面板，他名下的 Data/Stickers/runtimes 不会被清理（需要手动删或各用户自行卸载）。
; 这是「机器级安装 + 每用户数据」组合的固有结果，不影响安装/卸载流程本身。
;
; 【可再生的缓存：无条件删除】
; 便携版 Node 运行时：%LOCALAPPDATA%\DshManager\runtimes\node，由面板自行下载，重装后可
; 重新获取，体积可能上百 MB，卸载后留在盘上纯属垃圾。
; 路径依据：DshManager\Services\NodeRuntimeService.cs 的 GetDefaultRuntimesRoot()
;           → Path.Combine(localAppData, "DshManager", "runtimes", "node")
Type: filesandordirs; Name: "{localappdata}\DshManager\runtimes"

; 【用户数据：不在这里无条件删除】
;  - %LOCALAPPDATA%\DshManager\Data     面板设置 / 记忆库等
;    依据：DshManager\Services\JsonStoreService.cs 的 GetDefaultDataDirectory()
;  - %LOCALAPPDATA%\DshManager\Stickers 用户表情目录
;    依据：DshManager\Services\StickerService.cs 的 GetDefaultUserDirectory()
;    说明：任务书把 Stickers 归为「可再生缓存」，但程序里它是「用户自己放表情的目录」，
;    用户丢进去的是自己收集的图，删掉不可恢复，所以这里与 Data 同等对待、交给用户确认。
; 为什么不直接用 [Tasks] 勾选项控制（任务书建议的做法）：Inno 的 [Tasks] 只在安装阶段
; 生效，卸载器没有任务页，[UninstallDelete] 也不支持 Tasks 参数，声明式写法做不到。
; 因此改在 [Code] 段的 InitializeUninstall 里弹一次确认框（默认「否」= 保留），
; 静默卸载时绝不弹窗、一律保留（见下方注释）。

[Code]
{ ==========================================================================
  卸载阶段的清理与进程结束逻辑放在 [Code] 里的原因：
   1) 是否删除用户数据（Data / Stickers）需要用户确认，而 Inno 的卸载器没有任务页，
      声明式段无法表达「条件删除」；
   2) taskkill /IM 是按映像名做全机全用户匹配，会误杀 bin\Debug 下正在调试的实例或
      机器上任何同名进程，必须按可执行文件完整路径精确匹配后才能结束。
  ========================================================================== }

var
  { 用户在卸载确认框里是否选择了「同时删除面板设置与数据」（默认否） }
  RemoveUserData: Boolean;

{ 精确结束「本次安装目录下的那一个」面板实例。
  实现：用 PowerShell 先按进程名取候选，再按每个进程的 $_.Path 与安装目录下那个
  DshManager.exe 的完整路径精确比对（注意：Pascal 段里的花括号是注释符，所以本注释里
  不能出现 Inno 常量的花括号写法，需要时用文字描述）。
  取舍说明：
    - 只结束这一个路径的进程：bin\Debug 下的调试实例、机器上同名的其它程序都不受影响
      （这正是原 taskkill /IM 写法的缺陷，旁边那条「仅结束面板自身」的注释与实际不符，
      已随本段一起删除）。
    - 读不到 Path 的进程（其它用户所有 / 权限不足）会被 Where-Object 过滤掉，即「不杀」：
      最坏情况只是卸载时文件被占用，Inno 会照常提示，而不会误杀别人的进程。
    - 不用 WMI/Toolhelp32 的 P/Invoke 方案：那需要手写结构体声明，本机无法真机验证卸载
      流程，出错风险高于收益；PowerShell 方案可编译可读，且在受支持的 Windows 上都存在。
  返回 True 只表示「结束动作已执行」，不代表确实杀掉了进程。 }
function KillInstalledInstance(): Boolean;
var
  ResultCode: Integer;
  PsExe, PsParams: String;
begin
  PsExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  { 组装 PowerShell 命令：单引号字面量在 Pascal 里要写成两个单引号 }
  PsParams := '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "Get-Process -Name ''{#AppShortName}'' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' + ExpandConstant('{app}\{#AppExeName}') + ''' } | Stop-Process -Force"';
  Result := Exec(PsExe, PsParams, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then
    Log('结束面板进程：powershell 退出码 = ' + IntToStr(ResultCode))
  else
    Log('结束面板进程失败：无法启动 ' + PsExe);
end;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
  Root: String;
begin
  Result := True;            { 返回 True 才继续卸载；这里不做任何阻断 }
  RemoveUserData := False;   { 默认 = 保留用户数据 }
  Root := ExpandConstant('{localappdata}\DshManager');

  { 先尝试结束面板实例，避免卸载时程序文件被占用 }
  if not KillInstalledInstance() then
  begin
    { 【不静默继续】原脚本用 runhidden 调 taskkill，起不来也毫无提示。这里明确告知用户，
      并给出人工处理方式，而不是假装一切正常。 }
    MsgBox('未能自动结束正在运行的面板进程（无法启动 PowerShell）。' + #13#10 +
           '如果卸载过程中提示文件被占用，请手动关闭面板后重试。',
           mbInformation, MB_OK);
  end;

  { 【静默卸载绝不弹窗】/SILENT、/VERYSILENT 下无人值守，弹窗会把卸载卡死；
    此时一律保留用户数据 —— 宁可留下可手动清理的目录，也不能让脚本挂住。 }
  if UninstallSilent then
  begin
    Log('静默卸载：保留 ' + Root + '\Data 与 ' + Root + '\Stickers');
    Exit;
  end;

  Answer := MsgBox('是否同时删除面板的设置、记忆库与表情数据？' + #13#10 + #13#10 +
                   Root + '\Data        （面板设置、记忆库等）' + #13#10 +
                   Root + '\Stickers    （用户表情）' + #13#10 + #13#10 +
                   '选择「否」将保留这些数据，方便以后重新安装时继续使用（默认）。',
                   mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
  RemoveUserData := (Answer = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Root: String;
begin
  { usPostUninstall = 程序文件与 [UninstallDelete] 都已处理完之后 }
  if CurUninstallStep <> usPostUninstall then Exit;
  Root := ExpandConstant('{localappdata}\DshManager');

  if RemoveUserData then
  begin
    { DelTree 第 4 个参数为 True：连子目录一起删 }
    DelTree(Root + '\Data', True, True, True);
    DelTree(Root + '\Stickers', True, True, True);
    Log('已按用户选择删除面板设置与表情目录');
  end
  else
    Log('用户选择保留面板设置与表情目录：' + Root);

  { 顺手清掉空壳目录：%LOCALAPPDATA%\DshManager 里若已无内容（Data/Stickers/runtimes 都没了）
    才删得掉；RemoveDir 对非空目录返回 False，这里失败属正常，忽略即可。 }
  if not RemoveDir(Root) then
    Log('保留目录（仍非空或不存在）：' + Root);
end;
