# _task-test.ps1 —— 验证 /MERGETASKS=desktopicon 是否真的会建桌面快捷方式
# ------------------------------------------------------------------
# 背景：正式安装时传了 /MERGETASKS=desktopicon，但桌面上没有出现 lnk。
#       第一次测试脚本里误用了 PowerShell 专有的 *>&1 | Tee-Object 当作进程参数，
#       结果日志是空的 —— 那次实验不可信，结论作废。
# 本脚本只做一件事：删掉桌面 lnk → 用裸值 /MERGETASKS=desktopicon 重装 → 看 lnk 回不回来。
# 写 transcript，避免再出现「没日志就说不清」的情况。
# ------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$SetupPath = '',
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Continue'
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP ('dshmgr-tasktest-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
}
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host "  用户=$($id.Name)  管理员=$isAdmin  日志=$LogPath"
    if (-not $isAdmin) { throw '未提权。' }

    if ([string]::IsNullOrWhiteSpace($SetupPath)) {
        $repo = Split-Path -Parent $PSScriptRoot
        $SetupPath = (Get-ChildItem -LiteralPath (Join-Path $repo 'installer\output') -Filter 'DshManager-Setup-*.exe' |
                      Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    Write-Host "  安装包 = $SetupPath"

    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desktop '鲸鱼娘 · DeepSeek Harness 管理面板.lnk'
    Write-Host "  桌面   = $desktop"

    if (Test-Path -LiteralPath $lnk) {
        Remove-Item -LiteralPath $lnk -Force
        Write-Host "  已删除桌面 lnk（作为实验起点）: 现在存在吗 = $(Test-Path -LiteralPath $lnk)"
    }
    else { Write-Host '  桌面 lnk 本来就不存在（作为实验起点）' }

    # 关键：裸值，不带引号；使用 -ArgumentList 数组形式避免引号处理歧义
    $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/MERGETASKS=desktopicon')
    Write-Host ''
    Write-Host "  安装参数: $($installArgs -join ' ')"
    $proc = Start-Process -FilePath $SetupPath -ArgumentList $installArgs -Wait -PassThru
    Write-Host "  安装器退出码 = $($proc.ExitCode)"

    Start-Sleep -Seconds 2
    $exists = Test-Path -LiteralPath $lnk
    Write-Host ''
    Write-Host "  >>> 结论：/MERGETASKS=desktopicon 是否创建了桌面快捷方式 = $exists"
    if ($exists) {
        $ws = New-Object -ComObject WScript.Shell
        Write-Host "      目标 = $($ws.CreateShortcut($lnk).TargetPath)"
        Write-Host "      （这将是 Inno 自己创建的，不是我手工建的）"
    }
}
catch {
    Write-Host "✗ 出错：$($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
}
finally {
    Stop-Transcript | Out-Null
}
