<#
.SYNOPSIS
    一键把 DshManager 发布并打包成 Inno Setup 安装包。

.DESCRIPTION
    仓库里原先只有 installer\DshManager.iss 顶部的注释描述构建步骤，没有任何脚本，
    构建不可复现。本脚本按固定顺序完成两件事：

      1) dotnet publish —— Release + 自包含 + win-x64。先发到「路径不含单引号」的中转目录
         （默认 C:\dshpub），再把内容整体拷进仓库根目录的 publish\
         为什么要中转：只要输出路径里含单引号（例如仓库放在用户名带单引号的 Windows
         账号的桌面下），MSBuild 在 Microsoft.NET.Publish.targets:361 用
             @(_ResolvedFileToPublishPreserveNewest->'$(PublishDir)%(RelativePath)')
         计算目标文件，路径中的单引号会让这个项变换被解析坏掉，N 个源文件全部塌缩到同一个
         目标上，报 error MSB3094（DestinationFiles 1 项 / SourceFiles N 项）。
         实测对照：同一工程不传 -o 成功、-o 到无单引号目录成功、-o 到含单引号目录必失败；
         换成全新最小 WPF 工程同样复现 —— 与工程内容无关，只与输出路径有关。
         最终产物必须落在仓库 publish\，与 installer\DshManager.iss 的 Source: "..\publish\*" 一致。
      2) ISCC.exe 编译 installer\DshManager.iss
         产物：installer\output\DshManager-Setup-<版本>.exe

    打包前会校验版本号：DshManager\DshManager.csproj 的 <Version> 必须与
    installer\DshManager.iss 的 #define AppVersion 完全一致，不一致直接报错退出，
    避免打出「文件名与程序属性页版本对不上」的包。

.PARAMETER Configuration
    发布配置，默认 Release。

.PARAMETER RuntimeIdentifier
    发布 RID，默认 win-x64（与安装脚本的 ArchitecturesAllowed=x64compatible 对应）。

.PARAMETER StageDir
    dotnet publish 的中转目录，默认 C:\dshpub。**这个路径里绝对不能有单引号**，
    否则 MSB3094（原因见上）。发布完成后内容会被拷进 publish\，中转目录随即删除。

.PARAMETER SkipPublish
    跳过 dotnet publish，直接复用已有的 publish\ 目录重新打包（调试安装脚本时用）。

.PARAMETER OutputDir
    安装包输出目录；不指定则用 .iss 里的 OutputDir（installer\output）。

.EXAMPLE
    pwsh -File tools\build-installer.ps1

.EXAMPLE
    pwsh -File tools\build-installer.ps1 -SkipPublish

.EXAMPLE
    pwsh -File tools\build-installer.ps1 -OutputDir D:\out
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$StageDir = 'C:\dshpub',
    [switch]$SkipPublish,
    [string]$OutputDir
)

# 删除目录：PowerShell 5.1 的 Remove-Item -LiteralPath 不接受通配符，
# 而路径含 [ ] 等字符时又必须按字面量处理，所以统一走 .NET API。
function Remove-DirectoryTree {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    [System.IO.Directory]::Delete($Path, $true)
}

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$csprojPath = Join-Path $repoRoot 'DshManager\DshManager.csproj'
$issPath    = Join-Path $repoRoot 'installer\DshManager.iss'
$publishDir = Join-Path $repoRoot 'publish'

function Read-CsprojVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "找不到工程文件：$Path" }
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $m = [regex]::Match($text, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { throw "在 $Path 里没有找到 <Version>，无法确定版本号。" }
    return $m.Groups[1].Value
}

function Read-IssVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "找不到安装脚本：$Path" }
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $m = [regex]::Match($text, '#define\s+AppVersion\s+"([^"]+)"')
    if (-not $m.Success) { throw "在 $Path 里没有找到 #define AppVersion，无法确定版本号。" }
    return $m.Groups[1].Value
}

function Find-Iscc {
    # 按「常见安装位置 → 注册表 → PATH」的顺序探测；全部失败时抛出中文错误
    $candidates = New-Object System.Collections.ArrayList

    if ($env:ProgramFiles) { [void]$candidates.Add((Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) }
    if (${env:ProgramFiles(x86)}) { [void]$candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')) }
    if ($env:LOCALAPPDATA) { [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) }

    $regKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $regKeys) {
        try {
            $item = Get-ItemProperty -Path $key -Name 'InstallLocation' -ErrorAction Stop
            if ($item.InstallLocation) { [void]$candidates.Add((Join-Path $item.InstallLocation 'ISCC.exe')) }
        } catch {
            # 注册表项不存在属正常情况（可能通过其它方式安装），继续探测下一个
        }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw @"
没有找到 Inno Setup 编译器 ISCC.exe。
已尝试这些位置：
$($candidates | ForEach-Object { "  - $_" } | Out-String)
请先安装 Inno Setup 6：https://jrsoftware.org/isdl.php
安装后重新运行本脚本即可（默认会探测 Program Files / Program Files (x86) / 注册表里的安装位置）。
"@
}

function Find-DotnetSdk {
    # 本机实测：C:\Program Files\dotnet\dotnet.exe 只带了运行时（sdk 目录为空），真正带
    # 8.0.x SDK 的是用户级安装 %USERPROFILE%\.dotnet\dotnet.exe（8.0.425）。而 PATH 里
    # 前者通常排在前面，直接 Get-Command dotnet 会拿到没有 SDK 的那个，报错是
    # "No .NET SDKs were found"。所以这里逐个候选探测 --list-sdks，选第一个真有 SDK 的；
    # 都没有就抛中文错误，避免用户对着一句英文报错猜原因。
    $candidates = New-Object System.Collections.ArrayList
    if ($env:USERPROFILE) { [void]$candidates.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) }
    if ($env:ProgramFiles) { [void]$candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
    $onPath = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if ($onPath) { [void]$candidates.Add($onPath.Source) }

    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate)) { continue }
        # --list-sdks 的输出形如 "8.0.425 [C:\Users\...\.dotnet\sdk]"；没有 SDK 时以非 0 退出
        $output = & $candidate --list-sdks 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $output -match '(?m)^\s*\d+\.\d+\.\d+') {
            Write-Verbose "选中 dotnet：$candidate"
            return $candidate
        }
    }

    throw "没有找到带 .NET SDK 的 dotnet（只找到运行时）。已尝试：$($candidates -join '、')。请安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}

# ---- 1) 版本号一致性校验（唯一真源：DshManager.csproj 的 <Version>） -------------
$csprojVersion = Read-CsprojVersion -Path $csprojPath
$issVersion    = Read-IssVersion -Path $issPath
Write-Host "工程版本（DshManager.csproj <Version>）：$csprojVersion"
Write-Host "安装脚本版本（DshManager.iss AppVersion）：$issVersion"
if ($csprojVersion -ne $issVersion) {
    throw "版本号不一致：csproj=$csprojVersion，iss=$issVersion。请把 installer\DshManager.iss 顶部的 #define AppVersion 与 AppVersionQuad 改成与 csproj 一致后再打包。"
}

# ---- 2) 发布 ------------------------------------------------------------------
if (-not $SkipPublish) {
    # 必须发到「路径不含单引号」的中转目录：只要仓库完整路径里含单引号
    # （例如放在用户名带单引号的 Windows 账号下），直接 -o $publishDir（仓库下的 publish\）
    # 会让 MSBuild 的发布项变换
    #   @(_ResolvedFileToPublishPreserveNewest->'$(PublishDir)%(RelativePath)')
    # 解析失败，N 个源文件塌缩成 1 个目标，报 MSB3094。详见脚本头部说明。
    if ($StageDir -match "'") {
        throw "StageDir 路径里不能含单引号（MSB3094）：$StageDir"
    }

    Write-Host ""
    Write-Host "==> dotnet publish（$Configuration / $RuntimeIdentifier / 自包含）→ $StageDir"
    $dotnetExe = Find-DotnetSdk
    Write-Host "    使用 dotnet：$dotnetExe"

    # 说明：这里显式传 -r / --self-contained，而不是在 csproj 里声明 <RuntimeIdentifier>，
    # 免得改变日常 dotnet build 的输出路径（详见 DshManager.csproj 里的注释）。
    # publish 目录里的 *.pdb 由 installer\DshManager.iss 的 Excludes 排除，不额外处理。
    Remove-DirectoryTree -Path $StageDir
    if (-not (Test-Path -LiteralPath $StageDir)) {
        [void](New-Item -ItemType Directory -Path $StageDir -Force)
    }

    & $dotnetExe publish (Join-Path $repoRoot 'DshManager\DshManager.csproj') `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -o $StageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）。" }

    # 中转目录 → 仓库 publish\：整目录替换，避免旧版本残留文件混进安装包。
    Write-Host "    拷贝 $StageDir → $publishDir"
    Remove-DirectoryTree -Path $publishDir
    Copy-Item -LiteralPath $StageDir -Destination $publishDir -Recurse -Force
    Remove-DirectoryTree -Path $StageDir

    $stageCount = (Get-ChildItem -LiteralPath $publishDir -Recurse -File -ErrorAction SilentlyContinue).Count
    Write-Host "    publish\ 现有 $stageCount 个文件"
} else {
    Write-Host ""
    Write-Host "==> 跳过 dotnet publish（-SkipPublish），复用已有 publish\ 目录"
}

$publishedExe = Join-Path $publishDir 'DshManager.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "publish\DshManager.exe 不存在（$publishedExe）。请去掉 -SkipPublish 重新发布。"
}

# ---- 3) 编译安装包 ------------------------------------------------------------
$iscc = Find-Iscc
Write-Host ""
Write-Host "==> ISCC：$iscc"
Write-Host "==> 编译：$issPath"

$isccArgs = @()
if ($OutputDir) {
    # ISCC 的 /O<目录> 覆盖 .iss 里的 OutputDir
    $isccArgs += "/O$OutputDir"
    $effectiveOutputDir = $OutputDir
} else {
    $effectiveOutputDir = Join-Path $repoRoot 'installer\output'
}
$isccArgs += $issPath

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）。" }

$setupPath = Join-Path $effectiveOutputDir "DshManager-Setup-$csprojVersion.exe"
if (Test-Path -LiteralPath $setupPath) {
    $sizeMiB = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "打包完成：$setupPath（$sizeMiB MiB）"
} else {
    Write-Host ""
    Write-Host "ISCC 已成功返回，但未在预期位置找到 $setupPath —— 请确认 -OutputDir / .iss 的 OutputDir 设置。"
}
