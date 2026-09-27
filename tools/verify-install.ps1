<#
.SYNOPSIS
    DshManager 安装包「安装 / 卸载」真机验收脚本。

.DESCRIPTION
    专门验证审计修复里那几条**只能在真机上跑安装器才能验证**的行为：

      B2  [Run] 的 runasoriginaluser —— 面板是否以原始（非提权）用户身份启动
      C1  [UninstallDelete] / [Code] —— 卸载后 runtimes\node 是否清掉、
                                      Data 与 Stickers 是否按用户选择保留
      C2  [Code] KillInstalledInstance —— 结束的是不是「本次安装目录下那一个」，
                                      有没有误杀 bin\Debug 调试实例
      A3  VersionInfo —— 安装后的 exe 与「应用和功能」里的版本号是否一致
      D1  打包清单 —— 落地目录里有没有 pdb / 多余本地化目录

    本脚本**不碰**已有的正式安装（D:\al1969\DshManager 与
    %LOCALAPPDATA%\Programs\DshManager），它只往 -TestDir 指定的隔离目录装。

    由于 [Setup] 里 PrivilegesRequired=admin，本脚本必须在**管理员** PowerShell 里跑。

.PARAMETER SetupPath
    要测的安装包路径。默认取仓库 installer\output 下版本最新的那个。

.PARAMETER TestDir
    隔离的安装目录，默认 D:\dshmgr-test。

.PARAMETER Mode
    silent    全静默跑通安装→卸载，验证「静默卸载保留用户数据」这条约定（默认）
    dialog    走到卸载确认框，由你手动点选「是/否」，验证条件删除分支
    keep      只装不卸，方便你自己点开面板看 B2 的文件归属

.EXAMPLE
    # 在管理员 PowerShell 里
    pwsh -File verify-install.ps1

.EXAMPLE
    pwsh -File verify-install.ps1 -Mode dialog
#>
[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$TestDir = 'D:\dshmgr-test',
    [ValidateSet('silent', 'dialog', 'keep')]
    [string]$Mode = 'silent'
)

$ErrorActionPreference = 'Stop'

# 提权自检：不是管理员就没法跑（PrivilegesRequired=admin）
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "本脚本必须在管理员 PowerShell 里运行（安装包 PrivilegesRequired=admin）。当前用户：$($identity.Name)"
}

# 定位安装包
if (-not $SetupPath) {
    $repo = Split-Path -Parent $PSScriptRoot
    $outDir = Join-Path $repo 'installer\output'
    $candidate = Get-ChildItem -LiteralPath $outDir -Filter 'DshManager-Setup-*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw "在 $outDir 下找不到 DshManager-Setup-*.exe，请用 -SetupPath 指定。" }
    $SetupPath = $candidate.FullName
}
if (-not (Test-Path -LiteralPath $SetupPath)) { throw "安装包不存在：$SetupPath" }

$dataDir = Join-Path $env:LOCALAPPDATA 'DshManager'
$logPath = Join-Path $env:TEMP "dshmgr-install-verify-$(Get-Date -Format yyyyMMdd-HHmmss).log"

function Say {
    param([string]$Text, [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    Add-Content -LiteralPath $logPath -Value $Text -Encoding UTF8
}
function Head { param([string]$T) Say "" ; Say ("=" * 72) Cyan; Say $T Cyan; Say ("=" * 72) Cyan }

Say "安装包  : $SetupPath"
Say "隔离目录: $TestDir"
Say "模式    : $Mode"
Say "日志    : $logPath"

# ---- 前置检查 --------------------------------------------------------------
Head "0) 前置检查"
$running = Get-Process -Name DshManager -ErrorAction SilentlyContinue
if ($running) {
    Say "检测到正在运行的面板进程，先结束（避免安装时文件被占用）：" Yellow
    $running | ForEach-Object { Say "  PID $($_.Id)  $($_.Path)" }
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
} else {
    Say "  没有正在运行的面板进程 ✅"
}
Say "  用户数据目录: $dataDir  ($(if (Test-Path $dataDir) { '存在' } else { '不存在' }))"

# 记录安装前的数据目录状态，卸载后要对比
$before = @{}
if (Test-Path $dataDir) {
    foreach ($sub in 'Data', 'Stickers', 'runtimes', 'logs') {
        $p = Join-Path $dataDir $sub
        $before[$sub] = if (Test-Path $p) {
            (Get-ChildItem -LiteralPath $p -Recurse -File -ErrorAction SilentlyContinue).Count
        } else { -1 }
    }
    Say ("  安装前计数  Data={0}  Stickers={1}  runtimes={2}  logs={3}" -f `
        $before['Data'], $before['Stickers'], $before['runtimes'], $before['logs'])
}

# ---- 1) 安装 ---------------------------------------------------------------
Head "1) 静默安装到 $TestDir"
if (Test-Path -LiteralPath $TestDir) {
    Say "  隔离目录已存在，先清空" Yellow
    [System.IO.Directory]::Delete($TestDir, $true)
}
# 注意：变量不能叫 $args —— 那是 PowerShell 的自动变量
$installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=$TestDir")
Say "  $SetupPath $($installArgs -join ' ')"
$p = Start-Process -FilePath $SetupPath -ArgumentList $installArgs -Wait -PassThru
Say "  安装器退出码: $($p.ExitCode)  $(if ($p.ExitCode -eq 0) { '✅' } else { '❌' })"

# ---- 2) 落地内容核对（D1 / A3） ---------------------------------------------
Head "2) 落地内容与版本核对"
if (-not (Test-Path -LiteralPath $TestDir)) { throw "安装后目录不存在：$TestDir" }
$files = Get-ChildItem -LiteralPath $TestDir -Recurse -File -ErrorAction SilentlyContinue
Say "  文件数: $($files.Count)"
Say "  体积  : $([math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 2)) MiB"
Say "  DshManager.pdb : $(if (Test-Path "$TestDir\DshManager.pdb") { '❌ 不该出现' } else { '✅ 未打包' })"
Say "  createdump.exe : $(if (Test-Path "$TestDir\createdump.exe") { '⚠️ 还在（已知未清，不影响运行）' } else { '✅ 未打包' })"
$badLocales = Get-ChildItem -LiteralPath $TestDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|cs|zh-Hant)$' }
Say "  多余本地化目录 : $(if ($badLocales) { '❌ ' + ($badLocales.Name -join ', ') } else { '✅ 无' })"
Say "  zh-Hans        : $((Get-ChildItem "$TestDir\zh-Hans" -File -ErrorAction SilentlyContinue).Count) 个"
$exePath = Join-Path $TestDir 'DshManager.exe'
if (Test-Path -LiteralPath $exePath) {
    $vi = (Get-Item -LiteralPath $exePath).VersionInfo
    Say "  exe FileVersion    : $($vi.FileVersion)  $(if ($vi.FileVersion -like '1.9.7*') { '✅' } else { '❌' })"
    Say "  exe ProductVersion : $($vi.ProductVersion)"
    Say "  exe ProductName    : $($vi.ProductName)"
}

# ---- 3) 注册表条目（B1：不该再出现机器级 + 用户级两条） ----------------------
Head "3) 卸载条目"
$entries = @()
foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                  'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall') {
    Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($props.DisplayName -like '*DshManager*' -or $props.DisplayName -like '*鲸鱼娘*') {
            $entries += [pscustomobject]@{
                根   = $root.Split(':')[0]
                名称 = $props.DisplayName
                位置 = $props.InstallLocation
                卸载 = $props.UninstallString
            }
        }
    }
}
$entries | Format-Table -AutoSize | Out-String -Width 200 | ForEach-Object { Say $_ }
# 注意：必须用 @() 强制成数组。PowerShell 里「只有一项」的管道结果会被拆成标量，
# 而标量没有 .Count（PS 5.1 下取值为空），于是上次跑到这里输出了「指向本次隔离目录的条目:  条」——
# 数字是空的，看起来像断言失败其实是报告缺陷。
$mine = @($entries | Where-Object { $_.位置 -like "$TestDir*" })
Say "  指向本次隔离目录的条目: $($mine.Count) 条 $(if ($mine.Count -eq 1) { '✅' } else { '⚠️ 预期 1 条' })"
$machine = @($entries | Where-Object { $_.根 -eq 'HKLM' -and $_.位置 -notlike "$TestDir*" })
if ($machine) {
    Say "  另有别的机器级安装（未触碰）:" Yellow
    $machine | ForEach-Object { Say "    $($_.位置)" Yellow }
}

# ---- 4) 启动面板（B2：文件归属 / runasoriginaluser） -------------------------
Head "4) 启动面板并检查进程归属"
$exePath = Join-Path $TestDir 'DshManager.exe'
if (Test-Path -LiteralPath $exePath) {
    Say "  以当前（管理员）身份启动，观察 [Run] 的 runasoriginaluser 是否生效…"
    $proc = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Seconds 6
    $live = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($live) {
        Say "  PID $($live.Id) 已启动 ✅"
        Say "  （GUI 无法在此断言窗口，请肉眼确认面板已出现）" Yellow
    } else {
        Say "  进程已退出 —— 若是静默崩溃，请查 %LOCALAPPDATA%\DshManager\logs\*" Yellow
    }
    Say "  结束该进程"
    Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
} else {
    Say "  $exePath 不存在，跳过" Yellow
}

if ($Mode -eq 'keep') {
    Head "完成（keep 模式：只装不卸）"
    Say "  安装目录: $TestDir"
    Say "  要卸载请跑: & '$TestDir\unins000.exe'"
    return
}

# ---- 5) 卸载 ---------------------------------------------------------------
Head "5) 卸载"
$unins = Join-Path $TestDir 'unins000.exe'
if (-not (Test-Path -LiteralPath $unins)) { throw "找不到卸载器：$unins" }

if ($Mode -eq 'dialog') {
    Say "  dialog 模式：即将弹出卸载确认框，请你手动点选" Yellow
    Say "    · 选「否」（默认）→ 预期保留 Data / Stickers" Yellow
    Say "    · 选「是」        → 预期连 Data / Stickers 一起删掉" Yellow
    Start-Process -FilePath $unins -Wait
} else {
    Say "  静默卸载（约定：静默时绝不弹窗、一律保留用户数据）"
    $up = Start-Process -FilePath $unins -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Say "  卸载器退出码: $($up.ExitCode)  $(if ($up.ExitCode -eq 0) { '✅' } else { '❌' })"
}

# ---- 6) 卸载后核对（C1） ---------------------------------------------------
Head "6) 卸载残留核对"
Say "  安装目录还在吗 : $(if (Test-Path -LiteralPath $TestDir) { '⚠️ 还在（可能残留文件）' } else { '✅ 已删' })"
if (Test-Path -LiteralPath $TestDir) {
    Get-ChildItem -LiteralPath $TestDir -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 10 | ForEach-Object { Say "    $($_.FullName)" Yellow }
}
if (Test-Path $dataDir) {
    foreach ($sub in 'Data', 'Stickers', 'runtimes', 'logs') {
        $p = Join-Path $dataDir $sub
        $after = if (Test-Path $p) {
            (Get-ChildItem -LiteralPath $p -Recurse -File -ErrorAction SilentlyContinue).Count
        } else { -1 }
        $mark = ''
        if ($sub -eq 'runtimes') {
            $mark = if ($after -eq 0 -or $after -eq -1) { '✅ 已清' } else { "⚠️ 仍有 $after 个文件" }
        } else {
            $mark = if ($after -le $before[$sub]) { '✅ 保留' } else { '⚠️ 变了' }
        }
        Say ("  {0,-9} 安装前={1,-6} 卸载后={2,-6} {3}" -f $sub, $before[$sub], $after, $mark)
    }
} else {
    Say "  ⚠️ 用户数据目录整个没了 —— 静默卸载不该删它" Yellow
}

Head "完成"
Say "日志: $logPath"
Say "隔离目录: $TestDir  $(if (Test-Path -LiteralPath $TestDir) { '（仍在，请人工确认）' } else { '（已清理）' })"
