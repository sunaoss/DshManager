# _elevated-run.ps1 —— 提权跑腿脚本（临时）
# ------------------------------------------------------------------
# 用途：本脚本由非提权会话用 Start-Process -Verb RunAs 拉起，跑在管理员上下文里。
#       它做两件事：
#       1) 清掉那条悬空的 HKLM 卸载键（指向已删除的 %TEMP%\setup-portable-000152）
#       2) 点名叫 tools\verify-install.ps1 做完整的安装 → 版本核对 → 启动 → 卸载 → 残留核对
# 为什么要单独一个文件：非提权进程没法直接改 HKLM，也没法无提示安装
#       （安装包 PrivilegesRequired=admin）。UAC 必须在活人桌面上点「是」。
# 全程写 transcript 日志，跑完之后非提权会话直接读日志判读。
# ------------------------------------------------------------------
[CmdletBinding()]
param(
    # 被测安装包；留空则让 verify-install.ps1 自己取 installer\output 下最新的
    [string]$SetupPath = '',
    # 安装到哪（用 D 盘，不污染系统盘，且这个目录不存在、不会被误判成已安装）
    [string]$TestDir = 'D:\dshmgr-test',
    # silent = 全自动装卸；dialog = 卸载时留给你手点
    [ValidateSet('silent', 'dialog', 'keep')]
    [string]$Mode = 'silent',
    # 本脚本自己的日志
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Continue'

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP ("dshmgr-elevated-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
}

Start-Transcript -Path $LogPath -Force | Out-Null

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('=' * 70)
    Write-Host "  $text"
    Write-Host ('=' * 70)
}

try {
    Write-Step '提权自检'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host "  用户     : $($identity.Name)"
    Write-Host "  是否管理员: $isAdmin"
    Write-Host "  当前时间 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Host "  日志路径 : $LogPath"
    if (-not $isAdmin) {
        Write-Host '  ✗ 没有拿到管理员权限，后续步骤必然失败。'
        throw '未获得管理员权限。'
    }
    Write-Host '  ✓ 已提权。'

    # ------------------------------------------------------------------
    Write-Step '步骤 1/2：清理悬空的 HKLM 卸载键'
    $danglingKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A64-9E17-4B52-9C1D-2A6E7F4B0D31}_is1'
    if (Test-Path $danglingKey) {
        $props = Get-ItemProperty $danglingKey
        Write-Host "  DisplayName     = $($props.DisplayName)"
        Write-Host "  InstallLocation = $($props.InstallLocation)"
        $loc = $props.InstallLocation
        if ($loc -and -not (Test-Path -LiteralPath $loc)) {
            Remove-Item -Path $danglingKey -Recurse -Force -ErrorAction Stop
            Write-Host "  ✓ 已删除（目录不存在，确认是悬空条目）"
        }
        else {
            Write-Host "  ！该条目指向的目录仍然存在，为安全起见不删除。"
        }
    }
    else {
        Write-Host '  - 条目本来就不存在，无需清理。'
    }

    # ------------------------------------------------------------------
    Write-Step '步骤 2/2：调用 verify-install.ps1 做真机装卸验收'
    $verifyScript = Join-Path $PSScriptRoot 'verify-install.ps1'
    Write-Host "  脚本: $verifyScript"
    if (-not (Test-Path -LiteralPath $verifyScript)) {
        throw "找不到 verify-install.ps1：$verifyScript"
    }

    $verifyParams = @{
        TestDir = $TestDir
        Mode    = $Mode
    }
    if (-not [string]::IsNullOrWhiteSpace($SetupPath)) {
        $verifyParams['SetupPath'] = $SetupPath
    }
    Write-Host "  参数: TestDir=$TestDir  Mode=$Mode  SetupPath=$(if ($SetupPath) { $SetupPath } else { '(自动取最新)' })"
    Write-Host ''

    # 用 & 在当前作用域调用，让它的输出直接进 transcript。
    # 注意：脚本（.ps1）调用不是原生进程，$LASTEXITCODE 对它不更新，所以不能拿它当退出码
    # （上次就输出了「verify-install.ps1 退出码 =」后面一片空白）。改用 $? 取成功与否，
    # 真正要看的是 verify-install 自己打印的各段核对结论。
    & $verifyScript @verifyParams
    Write-Host ''
    Write-Host "  verify-install.ps1 调用结果 = $(if ($?) { '成功（无未捕获异常）' } else { '失败（有未捕获异常，见上方输出）' })"

    Write-Step '跑腿脚本执行完毕'
    Write-Host '  请把这个日志文件的内容交给鲸鱼娘判读。'
}
catch {
    Write-Host ''
    Write-Host "✗ 出错：$($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
}
finally {
    Stop-Transcript | Out-Null
}
