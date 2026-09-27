# =============================================================================
#  gen-stickers.ps1
#  用 GDI+（System.Drawing）以「纯代码」绘制 DeepSeek 鲸鱼娘 6 张 chibi 表情贴纸。
#   · 512x512，真 PNG（Format32bppArgb），透明背景
#   · 不依赖任何外部素材 / 字体，全部由椭圆、贝塞尔曲线、多边形路径绘制
#   · 运行： pwsh -File tools\gen-stickers.ps1
# =============================================================================
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'DshManager\Assets\Stickers'
$SIZE   = 512
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# ============================== 调色板 ==============================
function Col([int]$r, [int]$g, [int]$b, [int]$a = 255) {
    [System.Drawing.Color]::FromArgb($a, $r, $g, $b)
}
$cSkin    = Col 253 231 217          # 肤色
$cHair1   = Col 129 212 250          # 发色亮部 #81D4FA
$cHair2   = Col   2 136 209          # 发色暗部 #0288D1
$cLine    = Col  30  58  95          # 描边深蓝 #1E3A5F
$cBlush   = Col 255 128 171 205      # 腮红 #FF80AB
$cIris    = Col  47  72 128          # 瞳色
$cIrisLt  = Col 122 197 240          # 瞳底高光
$cPupil   = Col  26  35  64          # 瞳孔
$cWhite   = Col 255 255 255
$cMouth   = Col 158  62  78          # 口腔
$cTongue  = Col 255 138 160          # 舌头
$cCloth   = Col 205 235 252          # 衣服
$cCloth2  = Col 150 205 238          # 袖子 / 碗口
$cRed     = Col 229  57  53          # 怒气符号
$cSweat   = Col 150 214 247          # 汗滴
$cPink    = Col 255 128 171
$cRim     = Col 236 246 254          # 碗

# ============================== 画刷 ==============================
$bSkin   = [System.Drawing.SolidBrush]::new($cSkin)
$bBlush  = [System.Drawing.SolidBrush]::new($cBlush)
$bIris   = [System.Drawing.SolidBrush]::new($cIris)
$bIrisLt = [System.Drawing.SolidBrush]::new($cIrisLt)
$bPupil  = [System.Drawing.SolidBrush]::new($cPupil)
$bWhite  = [System.Drawing.SolidBrush]::new($cWhite)
$bLine   = [System.Drawing.SolidBrush]::new($cLine)
$bMouth  = [System.Drawing.SolidBrush]::new($cMouth)
$bTongue = [System.Drawing.SolidBrush]::new($cTongue)
$bCloth2 = [System.Drawing.SolidBrush]::new($cCloth2)
$bRed    = [System.Drawing.SolidBrush]::new($cRed)
$bSweat  = [System.Drawing.SolidBrush]::new($cSweat)
$bPink   = [System.Drawing.SolidBrush]::new($cPink)
$bRim    = [System.Drawing.SolidBrush]::new($cRim)

# ============================== 画笔 ==============================
function NewPen([System.Drawing.Color]$c, [double]$w) {
    $p = [System.Drawing.Pen]::new($c, $w)
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $p
}
$pLine   = NewPen $cLine    5    # 主描边
$pLine3  = NewPen $cLine    3.5  # 细描边
$pEye    = NewPen $cLine    4.5  # 眼圈
$pLid    = NewPen $cLine    9    # 上眼睑 / 半睁眼睑
$pBrow   = NewPen $cLine    8    # 眉毛
$pMouth  = NewPen $cLine    4    # 嘴线
$pHappy  = NewPen $cLine    9    # 弯月眼
$pSleep  = NewPen $cLine    9    # 困倦闭眼
$pHair   = NewPen $cHair1   9    # 呆毛内色
$pAhoge  = NewPen $cLine    17   # 呆毛描边
$pZ      = NewPen $cLine    11   # Z 描边
$pZ2     = NewPen $cHair1   5    # Z 内色
$pSym    = NewPen $cLine    14   # 符号描边
$pPinkP  = NewPen $cPink    8    # 符号内色
$pRed    = NewPen $cRed     9    # 怒气符号
$pSweat  = NewPen $cLine    3.5
$pBlushL = NewPen (Col 255 128 171 235) 5
$pLimbO  = NewPen $cLine    36   # 手臂描边
$pLimb   = NewPen $cCloth2  26   # 手臂内色

# 头发竖向渐变（渐变范围覆盖全部发型区域，避免边缘平铺出现色带）
$hairRect = [System.Drawing.Rectangle]::new(40, 10, 440, 500)
$gHair = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    $hairRect, $cHair1, $cHair2, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)

# =============================================================================
#  绘制部件（画布坐标固定，主角区：头顶 y≈60 ~ 下巴 y≈405，肩部 y≈404 起）
# =============================================================================
function Draw-BackHair($g) {
    # 后发主块：比脸更大的一圈，形成发型轮廓
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddEllipse(86, 60, 340, 380)
    $g.FillPath($gHair, $p); $g.DrawPath($pLine, $p)
    # 两侧垂到肩下的长发尖
    foreach ($x in @(1, -1)) {
        $t = [System.Drawing.Drawing2D.GraphicsPath]::new()
        if ($x -eq 1) {
            $t.AddBezier(104, 330, 72, 404, 84, 466, 116, 502)
            $t.AddBezier(116, 502, 150, 470, 156, 402, 144, 336)
        } else {
            $t.AddBezier(408, 330, 440, 404, 428, 466, 396, 502)
            $t.AddBezier(396, 502, 362, 470, 356, 402, 368, 336)
        }
        $t.CloseFigure()
        $g.FillPath($gHair, $t); $g.DrawPath($pLine, $t)
    }
}

function Draw-Fins($g) {
    # 头顶两侧的小鲸鱼鳍
    foreach ($x in @(1, -1)) {
        $f = [System.Drawing.Drawing2D.GraphicsPath]::new()
        if ($x -eq 1) {
            $f.AddBezier(368, 232, 414, 224, 444, 190, 450, 158)
            $f.AddBezier(450, 158, 414, 182, 384, 204, 368, 232)
        } else {
            $f.AddBezier(144, 232, 98, 224, 68, 190, 62, 158)
            $f.AddBezier(62, 158, 98, 182, 128, 204, 144, 232)
        }
        $f.CloseFigure()
        $g.FillPath($gHair, $f); $g.DrawPath($pLine, $f)
        # 鳍上的两根纹理线
        if ($x -eq 1) {
            $g.DrawBezier($pLine3, 384, 226, 406, 212, 424, 192, 434, 170)
            $g.DrawBezier($pLine3, 396, 228, 418, 214, 434, 196, 442, 176)
        } else {
            $g.DrawBezier($pLine3, 128, 226, 106, 212, 88, 192, 78, 170)
            $g.DrawBezier($pLine3, 116, 228, 94, 214, 78, 196, 70, 176)
        }
    }
}

function Draw-Body($g) {
    # 圆肩上半身
    $b = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $b.AddBezier(146, 512, 168, 430, 210, 404, 256, 404)
    $b.AddBezier(256, 404, 302, 404, 344, 430, 366, 512)
    $b.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new($cCloth), $b); $g.DrawPath($pLine, $b)
    # 领口 V 字
    $g.DrawLine($pLine3, 216, 414, 256, 454)
    $g.DrawLine($pLine3, 296, 414, 256, 454)
}

function Draw-Face($g) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddEllipse(104, 95, 304, 310)
    $g.FillPath($bSkin, $p); $g.DrawPath($pLine, $p)
}

function Draw-FrontHair($g) {
    # 1) 圆顶发帽：椭圆上部裁切到 y=160 的平底
    $dome = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $dome.AddArc(94, 70, 324, 350, 330.9, -121.9)
    $dome.AddLine(111.9, 160, 400.1, 160)
    $dome.CloseFigure()
    $g.FillPath($gHair, $dome); $g.DrawPath($pLine, $dome)

    # 2) 刘海发片：4 片下垂尖角（中分感）
    foreach ($x in @(@(150, 112, 206), @(222, 104, 194), @(290, 104, 194), @(362, 112, 206))) {
        $cx = $x[0]; $w = $x[1]; $by = $x[2]; $top = 108.0
        $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $p.AddBezier($cx - $w / 2, $top, $cx - $w / 2, $top + ($by - $top) * 0.55, $cx - $w * 0.30, $by, $cx, $by)
        $p.AddBezier($cx, $by, $cx + $w * 0.30, $by, $cx + $w / 2, $top + ($by - $top) * 0.55, $cx + $w / 2, $top)
        $p.CloseFigure()
        $g.FillPath($gHair, $p); $g.DrawPath($pLine, $p)
    }

    # 3) 左右鬓发：框住脸颊两侧
    foreach ($x in @(1, -1)) {
        $s = [System.Drawing.Drawing2D.GraphicsPath]::new()
        if ($x -eq 1) {
            $s.AddBezier(118, 168, 88, 250, 92, 346, 108, 440)
            $s.AddBezier(108, 440, 150, 426, 164, 352, 158, 256)
            $s.AddBezier(158, 256, 150, 212, 134, 182, 118, 168)
        } else {
            $s.AddBezier(394, 168, 424, 250, 420, 346, 404, 440)
            $s.AddBezier(404, 440, 362, 426, 348, 352, 354, 256)
            $s.AddBezier(354, 256, 362, 212, 378, 182, 394, 168)
        }
        $s.CloseFigure()
        $g.FillPath($gHair, $s); $g.DrawPath($pLine, $s)
    }

    # 4) 呆毛：深色粗描边 + 亮色内芯
    $g.DrawBezier($pAhoge, 250, 140, 240, 70, 270, 20, 300, 34)
    $g.DrawBezier($pHair,  250, 140, 240, 70, 270, 20, 300, 34)
}

function Draw-Blush($g, [double]$cx, [double]$cy, [double]$w = 64, [double]$h = 34) {
    $g.FillEllipse($bBlush, $cx - $w / 2, $cy - $h / 2, $w, $h)
}
function Draw-BlushLines($g, [double]$cx, [double]$cy) {
    foreach ($dx in @(-14, 0, 14)) {
        $g.DrawLine($pBlushL, $cx + $dx, $cy - 12, $cx + $dx, $cy + 12)
    }
}

# 睁眼（$dx 为视线偏移：>0 向右看，<0 向左看）；虹膜裁剪在眼型内防止溢出
function Draw-OpenEye($g, [double]$cx, [double]$cy, [double]$w, [double]$h, [double]$dx) {
    $g.FillEllipse($bWhite, $cx - $w / 2, $cy - $h / 2, $w, $h)
    $clip = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $clip.AddEllipse($cx - $w / 2, $cy - $h / 2, $w, $h)
    $g.SetClip($clip)
    $g.FillEllipse($bIris,   $cx - $w * 0.44 + $dx, $cy - $h * 0.46, $w * 0.88, $h * 0.92)
    $g.FillEllipse($bIrisLt, $cx - $w * 0.34 + $dx, $cy + $h * 0.08, $w * 0.68, $h * 0.30)
    $g.FillEllipse($bPupil,  $cx - $w * 0.17 + $dx, $cy - $h * 0.12, $w * 0.34, $h * 0.42)
    $g.FillEllipse($bWhite,  $cx - $w * 0.38 + $dx, $cy - $h * 0.42, $w * 0.32, $h * 0.30)
    $g.FillEllipse($bWhite,  $cx + $w * 0.12 + $dx, $cy + $h * 0.16, $w * 0.16, $h * 0.14)
    $g.ResetClip()
    $g.DrawEllipse($pEye, $cx - $w / 2, $cy - $h / 2, $w, $h)
    $g.DrawArc($pLid, $cx - $w / 2, $cy - $h / 2, $w, $h, 185, 170)   # 加粗上眼睑
}

# 闭眼：$up=$true 为 ^ ^（开心），$false 为 u u（困倦）
function Draw-ClosedEye($g, [double]$cx, [double]$cy, [bool]$up) {
    $x = $cx - 44; $y = $cy - 32; $w = 88; $h = 68
    if ($up) { $g.DrawArc($pHappy, $x, $y, $w, $h, 200, 140) }
    else     { $g.DrawArc($pSleep, $x, $y, $w, $h,  20, 140) }
}

# 压低的上眼睑（半睁眼），裁剪在眼型内
function Draw-HalfLid($g, [double]$cx, [double]$cy, [double]$w, [double]$h, [double]$tilt) {
    $clip = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $clip.AddEllipse($cx - $w / 2, $cy - $h / 2, $w, $h)
    $g.SetClip($clip)
    $g.DrawLine($pLid, $cx - $w / 2, $cy - $h * 0.26 + $tilt, $cx + $w / 2, $cy - $h * 0.26 - $tilt)
    $g.ResetClip()
}

# 眉毛（两端点间向上微拱）
function Draw-Brow($g, [double]$x1, [double]$y1, [double]$x2, [double]$y2) {
    $mx = ($x1 + $x2) / 2; $my = ($y1 + $y2) / 2 - 9
    $g.DrawBezier($pBrow, $x1, $y1, $mx, $my, $mx, $my, $x2, $y2)
}

function Draw-Sparkle($g, [double]$cx, [double]$cy, [double]$r) {
    # 四角星（凹边的八角多边形）
    $k = 0.22
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, $cy - $r),
        [System.Drawing.PointF]::new($cx + $r * $k, $cy - $r * $k),
        [System.Drawing.PointF]::new($cx + $r, $cy),
        [System.Drawing.PointF]::new($cx + $r * $k, $cy + $r * $k),
        [System.Drawing.PointF]::new($cx, $cy + $r),
        [System.Drawing.PointF]::new($cx - $r * $k, $cy + $r * $k),
        [System.Drawing.PointF]::new($cx - $r, $cy),
        [System.Drawing.PointF]::new($cx - $r * $k, $cy - $r * $k))
    $g.FillPolygon($bWhite, $pts)
    $g.DrawPolygon($pLine3, $pts)
}

function Draw-Note($g, [double]$cx, [double]$cy, [double]$s = 1.0) {
    # 音符：符头 + 符干 + 符尾
    $g.FillEllipse($bLine, $cx - 14 * $s, $cy - 10 * $s, 28 * $s, 20 * $s)
    $g.FillRectangle($bLine, $cx + 8 * $s, $cy - 58 * $s, 7 * $s, 52 * $s)
    $f = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $f.AddBezier($cx + 15 * $s, $cy - 58 * $s, $cx + 46 * $s, $cy - 46 * $s, $cx + 44 * $s, $cy - 22 * $s, $cx + 15 * $s, $cy - 30 * $s)
    $f.CloseFigure()
    $g.FillPath($bLine, $f)
}

function Draw-Sweat($g, [double]$cx, [double]$cy, [double]$r = 20) {
    # 泪滴形汗珠
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddBezier($cx, $cy - $r * 2.6, $cx + $r, $cy - $r * 0.9, $cx + $r, $cy, $cx, $cy + $r * 0.9)
    $p.AddBezier($cx, $cy + $r * 0.9, $cx - $r, $cy, $cx - $r, $cy - $r * 0.9, $cx, $cy - $r * 2.6)
    $p.CloseFigure()
    $g.FillPath($bSweat, $p); $g.DrawPath($pSweat, $p)
    $g.FillEllipse($bWhite, $cx + 2, $cy - $r * 0.35, 8, 13)
}

function Draw-AngerMark($g, [double]$cx, [double]$cy) {
    # 四笔弯线组成的井形怒气符号
    $g.DrawLine($pRed, $cx - 6,  $cy - 34, $cx - 16, $cy + 30)
    $g.DrawLine($pRed, $cx + 16, $cy - 32, $cx + 6,  $cy + 32)
    $g.DrawLine($pRed, $cx - 34, $cy - 6,  $cx + 30, $cy - 16)
    $g.DrawLine($pRed, $cx - 30, $cy + 18, $cx + 34, $cy + 8)
}

function Draw-Z($g, [double]$cx, [double]$cy, [double]$s) {
    # 用折线画字母 Z（不依赖字体）
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx - $s, $cy - $s),
        [System.Drawing.PointF]::new($cx + $s, $cy - $s),
        [System.Drawing.PointF]::new($cx - $s, $cy + $s),
        [System.Drawing.PointF]::new($cx + $s, $cy + $s))
    $g.DrawLines($pZ, $pts)
    $g.DrawLines($pZ2, $pts)
}

function Draw-Question($g, [double]$cx, [double]$cy) {
    # 问号：上方弧钩 + 短竖 + 圆点（先深色描边再压粉色）
    foreach ($f in @(
            @{ pen = $pSym;    dot = $bLine; r = 8 },
            @{ pen = $pPinkP;  dot = $bPink; r = 5 })) {
        $g.DrawArc($f.pen, $cx - 26, $cy, 52, 54, 160, 210)
        $g.DrawLine($f.pen, $cx + 26, $cy + 27, $cx + 2, $cy + 58)
        $g.FillEllipse($f.dot, $cx - $f.r, $cy + 70, $f.r * 2, $f.r * 2)
    }
}

function Draw-CrossedArms($g) {
    # 抱臂：两条前臂近水平地叠在胸前（先描边色后内色），末端补一只小手
    foreach ($a in @(
            @(172, 470, 232, 466, 304, 462, 348, 460),
            @(334, 498, 288, 494, 194, 490, 146, 486))) {
        $g.DrawBezier($pLimbO, $a[0], $a[1], $a[2], $a[3], $a[4], $a[5], $a[6], $a[7])
        $g.DrawBezier($pLimb,  $a[0], $a[1], $a[2], $a[3], $a[4], $a[5], $a[6], $a[7])
    }
    foreach ($h in @(@(306, 446), @(120, 472))) {
        $g.FillEllipse($bSkin, $h[0], $h[1], 56, 44)
        $g.DrawEllipse($pLine, $h[0], $h[1], 56, 44)
    }
}

function Draw-Cheeks($g) {
    # 鼓腮：先以肤色画出鼓包（无描边），再用“排除脸型”的裁剪只给溢出部分补描边，
    # 这样鼓包与脸型自然衔接，不会像贴着两个圆球。
    $face = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $face.AddEllipse(104, 95, 304, 310)
    foreach ($c in @(@(114, 314, 78, 70), @(320, 314, 78, 70))) {
        $g.FillEllipse($bSkin, $c[0], $c[1], $c[2], $c[3])
    }
    $g.SetClip($face, [System.Drawing.Drawing2D.CombineMode]::Exclude)
    $g.DrawArc($pLine, 114, 314, 78, 70, 90, 180)    # 左腮：外半圈
    $g.DrawArc($pLine, 320, 314, 78, 70, 270, 180)   # 右腮：外半圈
    $g.ResetClip()
}

function Draw-Bowl($g) {
    # 双手
    $g.FillEllipse($bSkin, 146, 452, 62, 48); $g.DrawEllipse($pLine, 146, 452, 62, 48)
    $g.FillEllipse($bSkin, 304, 452, 62, 48); $g.DrawEllipse($pLine, 304, 452, 62, 48)
    # 米饭堆
    $g.FillEllipse($bWhite, 180, 412, 152, 54)
    $g.DrawArc($pLine, 180, 412, 152, 54, 185, 170)
    # 饭粒
    foreach ($q in @(@(196, 428, 20, 14), @(228, 418, 20, 14), @(262, 426, 20, 14), @(300, 420, 20, 14))) {
        $g.FillEllipse($bRim, $q[0], $q[1], $q[2], $q[3])
    }
    # 碗身
    $body = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $body.AddBezier(168, 458, 178, 500, 214, 506, 256, 506)
    $body.AddBezier(256, 506, 298, 506, 334, 500, 344, 458)
    $body.CloseFigure()
    $g.FillPath($bRim, $body); $g.DrawPath($pLine, $body)
    # 碗口
    $g.FillEllipse($bRim, 168, 446, 176, 26); $g.DrawEllipse($pLine, 168, 446, 176, 26)
    # 碗身装饰弧
    $g.DrawArc($pLine3, 188, 466, 136, 40, 20, 140)
}

# =============================================================================
#  六种表情（eyes/blush/mouth/extras 组合）
# =============================================================================
function Draw-Expression($g, [string]$name) {
    $eyeL = 188.0; $eyeR = 324.0; $eyeY = 290.0
    switch ($name) {
        'happy' {
            Draw-ClosedEye $g $eyeL $eyeY $true
            Draw-ClosedEye $g $eyeR $eyeY $true
            Draw-Brow $g 156 232 220 228
            Draw-Brow $g 356 232 292 228
            Draw-Blush $g 150 336
            Draw-Blush $g 362 336
            # 张嘴大笑
            $m = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $m.AddBezier(212, 332, 224, 394, 288, 394, 300, 332)
            $m.CloseFigure()
            $g.FillPath($bMouth, $m)
            $g.SetClip($m)
            $g.FillEllipse($bTongue, 224, 358, 64, 32)
            $g.ResetClip()
            $g.DrawPath($pLine, $m)
            # 星星 + 音符
            Draw-Sparkle $g 84 86 26
            Draw-Sparkle $g 132 150 13
            Draw-Sparkle $g 430 96 28
            Draw-Sparkle $g 466 168 14
            Draw-Note $g 452 300 0.95
        }
        'tsundere' {
            # 侧目：虹膜整体偏向左侧 + 半睁眼
            Draw-OpenEye $g $eyeL $eyeY 84 94 -15
            Draw-OpenEye $g $eyeR $eyeY 84 94 -19
            Draw-HalfLid $g $eyeL $eyeY 84 94 -6
            Draw-HalfLid $g $eyeR $eyeY 84 94 -6
            Draw-Brow $g 152 230 224 240
            Draw-Brow $g 360 232 288 238
            # 撅嘴
            $g.DrawArc($pMouth, 234, 344, 44, 28, 200, 140)
            # 强烈脸红 + 腮红斜线
            Draw-Blush $g 146 334 72 40
            Draw-Blush $g 366 334 72 40
            Draw-BlushLines $g 146 334
            Draw-BlushLines $g 366 334
            # 汗滴（恼羞成怒）
            Draw-Sweat $g 438 140 18
            # 抱臂
            Draw-CrossedArms $g
        }
        'confused' {
            # 一大一小的眼睛
            Draw-OpenEye $g $eyeL 288 84 94 0
            Draw-OpenEye $g 330 294 58 60 0
            Draw-Brow $g 148 206 226 214    # 左眉抬高
            Draw-Brow $g 366 240 294 232    # 右眉压低
            # 波浪嘴
            $g.DrawBezier($pMouth, 220, 352, 236, 338, 246, 368, 258, 352)
            $g.DrawBezier($pMouth, 258, 352, 270, 336, 280, 366, 292, 352)
            Draw-Blush $g 152 338 58 30
            Draw-Blush $g 360 338 58 30
            Draw-Question $g 350 52
        }
        'angry' {
            Draw-Cheeks $g
            Draw-Blush $g 150 350 54 26
            Draw-Blush $g 362 350 54 26
            # 瞪眼：眼睛压窄
            Draw-OpenEye $g $eyeL 292 82 82 0
            Draw-OpenEye $g $eyeR 292 82 82 0
            Draw-HalfLid $g $eyeL 292 82 82 4
            Draw-HalfLid $g $eyeR 292 82 82 -4
            # 倒竖的怒眉（外高内低）
            Draw-Brow $g 152 222 228 246
            Draw-Brow $g 360 222 284 246
            # 生气大张的嘴：宽口 + 上排牙齿 + 舌头
            $m = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $m.AddEllipse(216, 330, 80, 46)
            $g.FillPath($bMouth, $m)
            $g.SetClip($m)
            $g.FillRectangle($bWhite, 214, 330, 84, 13)
            $g.FillEllipse($bTongue, 238, 358, 36, 22)
            $g.ResetClip()
            $g.DrawPath($pLine, $m)
            Draw-AngerMark $g 68 96
        }
        'slacking' {
            Draw-ClosedEye $g $eyeL 292 $false
            Draw-ClosedEye $g $eyeR 292 $false
            Draw-Brow $g 156 226 220 224
            Draw-Brow $g 356 226 292 224
            # 打哈欠的大嘴
            $m = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $m.AddEllipse(228, 330, 56, 64)
            $g.FillPath($bMouth, $m)
            $g.SetClip($m)
            $g.FillEllipse($bTongue, 236, 366, 40, 24)
            $g.ResetClip()
            $g.DrawPath($pLine, $m)
            Draw-Blush $g 148 344 60 30
            Draw-Blush $g 364 344 60 30
            # 头顶 Zzz
            Draw-Z $g 386 116 16
            Draw-Z $g 430 68 22
        }
        'eating' {
            Draw-Cheeks $g
            Draw-Blush $g 150 350 54 26
            Draw-Blush $g 362 350 54 26
            Draw-OpenEye $g $eyeL 288 80 88 0
            Draw-OpenEye $g $eyeR 288 80 88 0
            Draw-Brow $g 156 224 220 218
            Draw-Brow $g 356 224 292 218
            # 咀嚼中的小嘴
            $m = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $m.AddEllipse(234, 336, 46, 40)
            $g.FillPath($bMouth, $m)
            $g.SetClip($m)
            $g.FillEllipse($bTongue, 240, 358, 34, 16)
            $g.ResetClip()
            $g.DrawPath($pLine, $m)
            # 饭粒（粘在嘴边 / 脸颊上）
            Draw-Sparkle $g 452 200 16
            foreach ($q in @(@(214, 318, 16, 11), @(296, 320, 16, 11), @(128, 324, 16, 11), @(364, 324, 16, 11))) {
                $g.FillEllipse($bWhite, $q[0], $q[1], $q[2], $q[3])
                $g.DrawEllipse($pLine3, $q[0], $q[1], $q[2], $q[3])
            }
            # 捧着的小饭碗
            Draw-Bowl $g
        }
    }
}

# =============================================================================
#  生成
# =============================================================================
function New-Sticker([string]$name) {
    $bmp = [System.Drawing.Bitmap]::new($SIZE, $SIZE, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    Draw-BackHair $g
    Draw-Fins $g
    Draw-Body $g
    Draw-Face $g
    Draw-FrontHair $g
    Draw-Expression $g $name

    $path = Join-Path $outDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $path
}

$names = @('happy', 'tsundere', 'confused', 'angry', 'slacking', 'eating')
foreach ($n in $names) { New-Sticker $n | Out-Null }

# 生成结果自检：MD5 + PNG 文件头
Write-Host "`n===== 生成结果自检 ====="
foreach ($n in $names) {
    $p = Join-Path $outDir "$n.png"
    $md5 = (Get-FileHash $p -Algorithm MD5).Hash
    $head = (([System.IO.File]::ReadAllBytes($p))[0..7] | ForEach-Object { $_.ToString('X2') }) -join ' '
    "{0,-14} MD5={1}  head={2}" -f "$n.png", $md5, $head
}
