# _install-real.ps1 —— 正式安装脚本（提权运行）
# ------------------------------------------------------------------
# 与 tools\_elevated-run.ps1 的区别：那个是「装到隔离目录 → 验 → 卸」的验收流程，
# 本脚本是「真装到你平时用的位置」，装完不卸。
#
# 装到哪：C:\Program Files\DshManager（安装包的 DefaultDirName={autopf}\DshManager，
#         即 Inno 的 autopf 在 64 位安装模式下解析为 C:\Program Files）。
#         所以刻意不传 /DIR，让它走包自己的默认值 —— 这样装出来的位置才是
#         真实用户会得到的位置，而不是我指定的地方。
# 启动  ：[Run] 那一条带 runasoriginaluser 且带 skipifsilent，
#         所以 /VERYSILENT 下不会自动拉起面板（静默安装不弹「立即启动」）。
#         装完由本脚本以普通用户身份、按你原来那套习惯启动它。
# ------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$SetupPath = '',
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Continue'

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP ('dshmgr-real-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
}
Start-Transcript -Path $LogPath -Force | Out-Null

function Head([string]$t) {
    Write-Host ''
    Write-Host ('=' * 70)
    Write-Host "  $t"
    Write-Host ('=' * 70)
}

try {
    Head '提权自检'
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    $isAdmin = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host "  用户      : $($id.Name)"
    Write-Host "  是否管理员: $isAdmin"
    Write-Host "  日志      : $LogPath"
    if (-not $isAdmin) { throw '未获得管理员权限，无法安装。' }
    Write-Host '  ✓ 已提权。'

    Head '0) 安装前状态'
    $target = Join-Path $env:ProgramFiles 'DshManager'
    Write-Host "  目标目录        : $target"
    Write-Host "  目标已存在吗    : $(Test-Path -LiteralPath $target)"
    $running = Get-Process -Name DshManager -ErrorAction SilentlyContinue
    Write-Host "  面板进程        : $(if ($running) { "有 $($running.Count) 个，先结束" } else { '无' })"
    if ($running) { $running | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
    $dataDir = Join-Path $env:LOCALAPPDATA 'DshManager'
    Write-Host "  用户数据目录    : $dataDir ($(if (Test-Path -LiteralPath $dataDir) { '已存在，安装不会清它' } else { '不存在' }))"

    if ([string]::IsNullOrWhiteSpace($SetupPath)) {
        # 用 $PSScriptRoot（本脚本所在目录 = tools\）反推仓库根，避免把含单引号的用户名写进路径字符串。
        # 先前写成 '...\ao''ao''ao...' 是 C# 的转义写法，PowerShell 单引号串里不成立，已被此处取代。
        $repo = Split-Path -Parent $PSScriptRoot
        $cand = Get-ChildItem -LiteralPath (Join-Path $repo 'installer\output') -Filter 'DshManager-Setup-*.exe' -ErrorAction Stop |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $cand) { throw '在 installer\output 下找不到安装包。' }
        $SetupPath = $cand.FullName
    }
    Write-Host "  安装包          : $SetupPath"
    Write-Host "  包版本          : $((Get-Item -LiteralPath $SetupPath).VersionInfo.FileVersionRaw)"

    Head '1) 静默安装到默认位置'
    $installArgs = @(
        '/VERYSILENT'
        '/SUPPRESSMSGBOXES'
        '/NORESTART'
        '/MERGETASKS=desktopicon'   # 桌面快捷方式，和你原来那套一致
    )
    Write-Host "  $SetupPath $($installArgs -join ' ')"
    $proc = Start-Process -FilePath $SetupPath -ArgumentList $installArgs -Wait -PassThru
    Write-Host "  安装器退出码: $($proc.ExitCode)  $(if ($proc.ExitCode -eq 0) { '✅' } else { '❌' })"

    Head '2) 落地内容核对'
    if (-not (Test-Path -LiteralPath $target)) { throw "安装后目标目录不存在：$target" }
    $files = Get-ChildItem -LiteralPath $target -Recurse -File -ErrorAction SilentlyContinue
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host "  目录    : $target"
    Write-Host "  文件数  : $($files.Count)"
    Write-Host "  体积    : $([math]::Round($size / 1MB, 2)) MiB"
    $exe = Join-Path $target 'DshManager.exe'
    if (Test-Path -LiteralPath $exe) {
        $vi = (Get-Item -LiteralPath $exe).VersionInfo
        Write-Host "  exe FileVersion    : $($vi.FileVersion)"
        Write-Host "  exe ProductVersion : $($vi.ProductVersion)"
        Write-Host "  exe ProductName    : $($vi.ProductName)"
    }
    Write-Host "  DshManager.pdb : $(if (Test-Path -LiteralPath (Join-Path $target 'DshManager.pdb')) { '⚠️ 在' } else { '✅ 不在' })"
    $locales = Get-ChildItem -LiteralPath $target -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hant)$' }
    Write-Host "  多余语言目录   : $(if ($locales) { '⚠️ ' + ($locales.Name -join ', ') } else { '✅ 无' })"

    Head '3) 注册表卸载条目'
    $appid = '{8F3C2A64-9E17-4B52-9C1D-2A6E7F4B0D31}_is1'
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                      'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                      'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall') {
        $k = Join-Path $root $appid
        if (Test-Path $k) {
            $p = Get-ItemProperty $k
            Write-Host "  [$($root.Split(':')[0])] $($p.DisplayName)"
            Write-Host "        位置 = $($p.InstallLocation)"
            Write-Host "        卸载 = $($p.UninstallString)"
        }
    }

    Head '4) 快捷方式'
    $lnks = @()
    $lnks += Get-ChildItem (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue
    $lnks += Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue
    $lnks += Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter '*.lnk' -ErrorAction SilentlyContinue
    $ws = New-Object -ComObject WScript.Shell
    $hit = $lnks | Where-Object { $_.Name -like '*鲸鱼娘*' -or $_.Name -like '*DshManager*' }
    if ($hit) {
        foreach ($l in $hit) {
            $t = $ws.CreateShortcut($l.FullName).TargetPath
            Write-Host "  $($l.FullName)"
            Write-Host "      → $t  $(if (Test-Path -LiteralPath $t) { '✅ 目标在' } else { '❌ 目标不在' })"
        }
    } else { Write-Host '  （没找到快捷方式）' }

    Head '5) 启动面板（普通用户身份）'
    Write-Host '  面板启动后，请在它里面确认「dsh 安装路径」是否自动探测正确。'
    Write-Host '  注意：不要去恢复旧的 settings.json —— 它里面的 DshInstallPathOverride 指着'
    Write-Host "        一个已过期的 npx 目录（_npx\1e7f6d9597241db0），盖回去会让面板认不到 dsh。"

    Head '安装完成'
}
catch {
    Write-Host ''
    Write-Host "✗ 出错：$($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
}
finally {
    Stop-Transcript | Out-Null
}
