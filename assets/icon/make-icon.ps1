# ============================================================================
#  "TimeBack" application icon generator -- final design
#
#  ASCII-only source on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1
#  as GBK and non-ASCII comments then swallow the following line of code.
#
#  Parametric GDI+ drawing -> rasterised to every Windows icon size -> packed
#  into a multi-size .ico.  No third-party dependency.
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
#    powershell ... -File make-icon.ps1 -Variant flat
#    powershell ... -File make-icon.ps1 -Arrows hook
# ============================================================================

param(
    [ValidateSet('card','flat')] [string]$Variant = 'card',
    [ValidateSet('tri','hook')]  [string]$Arrows  = 'tri'
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$OutDir = Join-Path $PSScriptRoot ("icon_out_" + $Variant + "_" + $Arrows)
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# blue = current / file / state ; violet = time / rewind / past
$C = @{
    CardTopLeft = @(30,44,88);      CardBottomRight = @(48,26,108)
    EdgeStart   = @(58,120,232);    EdgeMid = @(140,96,240);   EdgeEnd = @(214,133,253)
    RingCyan    = @(72,206,244);    RingBlue = @(108,150,250)
    RingViolet  = @(152,122,250);   RingMagenta = @(212,118,250); RingPink = @(238,150,252)
    FolderTopLeft = @(132,168,254); FolderMid = @(126,132,246); FolderBottomRight = @(146,92,232)
    TabLeft = @(112,142,250);       TabRight = @(150,100,236)
    PanelTopLeft = @(58,78,178);    PanelMid = @(76,66,186);    PanelBottomRight = @(92,54,176)
    TextCore = @(255,255,255);      Glow = @(200,214,255)
    TickDark = @(22,26,64);         TickLight = @(186,206,252)
    InnerGlow = @(126,130,245)
    FoldSeam          = @(40, 30, 92)
    BackLeft          = @(96, 118, 236)
    BackMid           = @(122, 96, 238)
    BackRight         = @(168, 96, 240)
}

function New-Color([int[]]$rgb, [int]$a = 255) {
    return [System.Drawing.Color]::FromArgb($a, $rgb[0], $rgb[1], $rgb[2])
}
function PF([double]$x, [double]$y) { return New-Object System.Drawing.PointF ([float]$x),([float]$y) }

# Gradients are always built from two explicit endpoints, never from an angle:
# GDI+ angle constructors invert far too easily.
function New-LineBrush([double]$x0,[double]$y0,[double]$x1,[double]$y1,[int[]]$c0,[int[]]$c1,[int]$a = 255) {
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush (PF $x0 $y0),(PF $x1 $y1),(New-Color $c0 $a),(New-Color $c1 $a)
    $b.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    return $b
}
function New-StopBrush([double]$x0,[double]$y0,[double]$x1,[double]$y1,[int[][]]$stops,[double[]]$pos,[int]$a = 255) {
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush (PF $x0 $y0),(PF $x1 $y1),(New-Color $stops[0] $a),(New-Color $stops[$stops.Count-1] $a)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend $stops.Count
    $cols = @(); foreach ($s in $stops) { $cols += (New-Color $s $a) }
    $blend.Colors = $cols; $blend.Positions = $pos
    $b.InterpolationColors = $blend
    $b.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    return $b
}
function New-RoundedRect([double]$x,[double]$y,[double]$w,[double]$h,[double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r*2, [Math]::Min($w,$h))
    $p.AddArc([float]$x,[float]$y,[float]$d,[float]$d,180,90)
    $p.AddArc([float]($x+$w-$d),[float]$y,[float]$d,[float]$d,270,90)
    $p.AddArc([float]($x+$w-$d),[float]($y+$h-$d),[float]$d,[float]$d,0,90)
    $p.AddArc([float]$x,[float]($y+$h-$d),[float]$d,[float]$d,90,90)
    $p.CloseFigure(); return $p
}
function New-ArcBand([double]$cx,[double]$cy,[double]$rO,[double]$rI,[double]$a0,[double]$a1) {
    $n = [Math]::Max(10, [int]([Math]::Abs($a1-$a0)/2.5))
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($i=0; $i -le $n; $i++) { $a=($a0+($a1-$a0)*$i/$n)*[Math]::PI/180.0
        $pts.Add((PF ($cx+$rO*[Math]::Cos($a)) ($cy-$rO*[Math]::Sin($a)))) }
    for ($i=$n; $i -ge 0; $i--) { $a=($a0+($a1-$a0)*$i/$n)*[Math]::PI/180.0
        $pts.Add((PF ($cx+$rI*[Math]::Cos($a)) ($cy-$rI*[Math]::Sin($a)))) }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon($pts.ToArray()); return $p
}
function New-ArrowHead([double]$cx,[double]$cy,[double]$r,[double]$ang,[double]$hw,[double]$len,[int]$dir) {
    $a = $ang*[Math]::PI/180.0
    # Screen-space tangent for increasing angle.  p(a) = (cx + r cos a, cy - r sin a),
    # so dp/da = (-sin a, -cos a); with y pointing down that is CLOCKWISE.  The
    # counter-clockwise tangent is therefore (-sin a, +cos a) - sign matters!
    $tx = -[Math]::Sin($a)*$dir; $ty = [Math]::Cos($a)*$dir
    $nx =  [Math]::Cos($a);      $ny = -[Math]::Sin($a)
    $ax = $cx+$r*[Math]::Cos($a); $ay = $cy-$r*[Math]::Sin($a)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon(@((PF ($ax+$tx*$len) ($ay+$ty*$len)),(PF ($ax+$nx*$hw) ($ay+$ny*$hw)),(PF ($ax-$nx*$hw) ($ay-$ny*$hw))))
    return $p
}
function ScalePath([System.Drawing.Drawing2D.GraphicsPath]$src,[double]$k) {
    $d = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d.AddPath($src,$false)
    $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Scale([float]$k,[float]$k)
    $d.Transform($m); $m.Dispose(); return $d
}
function Detail-For([int]$s) { if ($s -le 20) { return 'tiny' }; if ($s -le 40) { return 'simple' }; return 'full' }

# ============================================================================
#  Draw one icon.  detail: 'full' | 'simple' | 'tiny'
#  Layout is designed on a 1024 grid with centre (512,520):
#    ring centreline radius 336, band 68 -> inner edge ~302
#    folder 380 x 316 centred           -> clear of the ring
#    inner panel 330 x 168              -> Ctrl+Z printed at ~74% of it
# ============================================================================
function Draw-Icon([int]$S, [string]$detail, [string]$variant, [string]$arrows) {

    $bmp = New-Object System.Drawing.Bitmap $S,$S,([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.SetResolution(96,96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $S/1024.0
    $withCard = ($variant -eq 'card')
    $showText = ($detail -ne 'tiny')
    $twoArcs  = ($detail -ne 'tiny')

    $cx = 512.0; $cy = 520.0
    $rRing = 352.0
    $ringW = 70.0
    if ($detail -eq 'simple') { $ringW = 84.0 }
    if ($detail -eq 'tiny')   { $ringW = 104.0 }
    $rIn = $rRing - $ringW/2
    $rOut = $rRing + $ringW/2

    $foldH = 168.0; $tabH = 76.0; $tabW = 165.0
    # tiny (16px): the folder must shrink or it swallows the ring
    if ($detail -eq 'tiny') { $foldH = 136.0; $tabH = 62.0; $tabW = 140.0 }
    $fx = $cx - $foldW/2; $fy = $cy - $foldH/2

    # Measured with GDI+ MeasureString: Segoe UI Bold 78px renders "Ctrl+Z" 268px
    # wide, i.e. 80% of the 336px inner panel.  Never eyeball this number.
    $fontPx = 67.0
    if ($detail -eq 'simple') { $fontPx = 75.0 }
    # ---- 0) optional card -------------------------------------------------
    if ($withCard) {
        $pad = 50.0; $cardW = 1024.0 - $pad*2
        $cardPath = ScalePath (New-RoundedRect $pad $pad $cardW $cardW 184) $k
        $cf = New-LineBrush $pad ($pad+$cardW) ($pad+$cardW) $pad $C.CardTopLeft $C.CardBottomRight
        $g.FillPath($cf,$cardPath); $cf.Dispose()
        $eb = New-StopBrush $pad ($pad+$cardW) ($pad+$cardW) $pad @($C.EdgeStart,$C.EdgeMid,$C.EdgeEnd) @(0.0,0.5,1.0)
        $ep = New-Object System.Drawing.Pen $eb,([float](8.0*$k))
        $ep.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($ep,$cardPath)
        $ep.Dispose(); $eb.Dispose(); $cardPath.Dispose()
        $gr = 292.0
        $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gp.AddEllipse([float](($cx-$gr)*$k),[float](($cy-$gr)*$k),[float]($gr*2*$k),[float]($gr*2*$k))
        $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp
        $pg.CenterColor = (New-Color $C.InnerGlow 56)
        $pg.SurroundColors = @((New-Color $C.InnerGlow 0))
        $g.FillPath($pg,$gp); $pg.Dispose(); $gp.Dispose()
    }

    # ---- 1) rewind ring ---------------------------------------------------
    # Gradient anchored on the ring bbox: cyan/blue lower-left -> pink upper-right.
    $rb = New-StopBrush ($cx-$rOut) ($cy+$rOut) ($cx+$rOut+$rOut) ($cy-$rOut-$rOut) @($C.RingCyan,$C.RingBlue,$C.RingViolet,$C.RingMagenta,$C.RingPink) @(0.0,0.20,0.38,0.58,1.0)

    # Heads at 10:30 / 4:30: balanced, and clear of both gradient extremes.
    # BOTH point counter-clockwise, so the ring carries ONE rotation direction
    # ("turning back") instead of the balanced pull of a refresh glyph.
    # Each head's arc terminates exactly AT the head, so the triangle base
    # always sits on the band - never a floating fragment.
    $headA = 135.0; $headB = 315.0

    # Arcs deliberately overshoot each head so the band is never cut: the
    # previous exact 145..315 / 45..135 pair left an 8 deg hole at 318-2 deg.
    # Keep every segment under 180 deg: an arc band spanning more than half the
    # circle self-intersects and GDI+ then leaves a hole in the fill.
    # 0..180 and 170..350 cover everything except 350..360, patched by a filler
    # arc; both seams end up underneath an arrow head.
    $bandB = ScalePath (New-ArcBand $cx $cy $rOut $rIn 170 350) $k
    $g.FillPath($rb,$bandB); $bandB.Dispose()
    $bandGap = ScalePath (New-ArcBand $cx $cy $rOut $rIn 344 360) $k
    $g.FillPath($rb,$bandGap); $bandGap.Dispose()

    if ($twoArcs) {
    $bandA = ScalePath (New-ArcBand $cx $cy $rOut $rIn 0 180) $k
        $g.FillPath($rb,$bandA); $bandA.Dispose()
    }

    # Half-width must stay inside the band (r 302..370): the old 1.24x made
    # the two base corners land at r=420 / r=252 and punched a hole in the ring.
    $ahHW  = $ringW * 0.62
    if ($detail -eq 'tiny') { $ahHW = $ringW * 0.78; $ahLen = $ringW * 1.20 }
    $ahLen = $ringW * 1.02
    if ($arrows -eq 'hook') { $ahLen = $ringW * 0.62 }

    $hA = ScalePath (New-ArrowHead $cx $cy $rRing $headA $ahHW $ahLen 1) $k
    $g.FillPath($rb,$hA); $hA.Dispose()
    if ($twoArcs) {
        $hB = ScalePath (New-ArrowHead $cx $cy $rRing $headB $ahHW $ahLen 1) $k
        $g.FillPath($rb,$hB); $hB.Dispose()
    }

    # ---- 2) time ticks ----------------------------------------------------
    # Outlined (dark casing under a light core) so they stay visible on BOTH
    # light and dark desktops - single-colour ticks vanished on one or other.
    if ($detail -eq 'full') {
        $rT = $rIn - 22
        $casing = New-Object System.Drawing.Pen ((New-Color $C.TickDark 170)),([float](13.0*$k))
        $core   = New-Object System.Drawing.Pen ((New-Color $C.TickLight 225)),([float](6.5*$k))
        foreach ($p in @($casing,$core)) {
            $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        }
        for ($i=0; $i -lt 24; $i++) {
            $ang = $i*15.0
            $len = 18.0
            if ($i % 6 -eq 0) { $len = 30.0 }
            $a = $ang*[Math]::PI/180.0
            $x1 = [float](($cx + $rT*[Math]::Cos($a))*$k);         $y1 = [float](($cy - $rT*[Math]::Sin($a))*$k)
            $x2 = [float](($cx + ($rT-$len)*[Math]::Cos($a))*$k);  $y2 = [float](($cy - ($rT-$len)*[Math]::Sin($a))*$k)
            $g.DrawLine($casing,$x1,$y1,$x2,$y2)
            $g.DrawLine($core,$x1,$y1,$x2,$y2)
        }
        $casing.Dispose(); $core.Dispose()
    }

    # ---- 3) central folder ------------------------------------------------
    # What actually makes a shape read as a FOLDER (learned over three
    # iterations, each failure recorded):
    #   1. flat single polygon with a raised top  -> "rounded rect + bump"
    #   2. trapezoid tab, shallow step            -> "roof"
    #   3. rectangular tab but back plate flush with the front on the right and
    #      bottom -> "card with a tab", still not a folder
    # The missing cue was ASYMMETRY: in a real folder the front flap is
    # smaller than the back, so the back plate stays visible along the right
    # and bottom edges.  That L-shaped silhouette is the recognisable part.
    #
    # back plate : tabW + tabExtra wide, foldH + backH taller
    # front flap : tabW wide, foldH tall, offset down-left so the back shows
    $tabExtra = 56.0
    $backH    = 42.0

    # --- back plate (visible: the tab on top, plus a strip down the right and bottom)
    $tp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tp.AddPolygon(@(
        (PF ($fx+18)                        ($fy+16)),
        (PF ($fx+$tabW-18)                  ($fy+16)),
        (PF ($fx+$tabW)                     ($fy+38)),
        (PF ($fx+$tabW+$tabExtra)           ($fy+$tabH)),
        (PF ($fx+$tabW+$tabExtra)           ($fy+$tabH+$foldH+$backH-14)),
        (PF ($fx)                           ($fy+$tabH+$foldH+$backH)),
        (PF ($fx)                           ($fy+$tabH))))
    $ts = ScalePath $tp $k
    $tb = New-StopBrush $fx ($fy+$tabH+$foldH) ($fx+$tabW+$tabExtra) ($fy+16) @($C.BackLeft,$C.BackMid,$C.BackRight) @(0.0,0.5,1.0)
    $g.FillPath($tb,$ts); $tb.Dispose(); $ts.Dispose(); $tp.Dispose()

    # --- front flap: the body.  Smaller than the back plate on purpose.
    $bodyTop = $fy + $tabH
    $fp = ScalePath (New-RoundedRect $fx $bodyTop ($tabW+$tabExtra) $foldH 34) $k
    $fb = New-StopBrush $fx ($bodyTop+$foldH) ($fx+$tabW+$tabExtra) $bodyTop @($C.FolderTopLeft,$C.FolderMid,$C.FolderBottomRight) @(0.0,0.5,1.0)
    $g.FillPath($fb,$fp); $fb.Dispose()

    # --- inner panel: the quiet ground for the Ctrl+Z symbol
    $panelW = 268.0; $panelH = 118.0
    $ix = $fx + ($tabW+$tabExtra)/2 - $panelW/2
    $iy = $bodyTop + 22
    $ip = ScalePath (New-RoundedRect $ix $iy $panelW $panelH 24) $k
    $ib = New-StopBrush $ix ($iy+$panelH) ($ix+$panelW) $iy @($C.PanelTopLeft,$C.PanelMid,$C.PanelBottomRight) @(0.0,0.5,1.0)
    $g.FillPath($ib,$ip); $ib.Dispose()

    # ---- 4) Ctrl+Z : single line, centred --------------------------------
    if ($showText) {
        $text = 'Ctrl+Z'
        $font = New-Object System.Drawing.Font 'Segoe UI',([float]($fontPx*$k)),([System.Drawing.FontStyle]::Bold),([System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $sf.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap
        # rect deliberately wider than the panel: only the centre matters, and
        # this makes glyph clipping impossible.
        $tr = New-Object System.Drawing.RectangleF ([float](($ix-70)*$k)),([float](($iy-8)*$k)),([float](($panelW+140)*$k)),([float]($panelH*$k))
        if ($detail -eq 'full') {
            foreach ($ga in @(48,32,18)) {
                $gb = New-Object System.Drawing.SolidBrush ((New-Color $C.Glow $ga))
                $g.DrawString($text,$font,$gb,$tr,$sf); $gb.Dispose()
            }
        }
        $tbr = New-Object System.Drawing.SolidBrush ((New-Color $C.TextCore 255))
        $g.DrawString($text,$font,$tbr,$tr,$sf)
        $tbr.Dispose(); $font.Dispose(); $sf.Dispose()
    }

    $rb.Dispose(); $fp.Dispose(); $ip.Dispose()
    $g.Dispose()
    return $bmp
}

# ============================================================================
#  Render
# ============================================================================
$icoOrder = @(256,128,64,48,32,24,16)

Write-Host ("=== variant: {0} / arrows: {1} ===" -f $Variant, $Arrows)
Write-Host '--- PNG ---'
foreach ($s in @(1024,512,256,128,64,48,32,24,16)) {
    $d = Detail-For $s
    $b = Draw-Icon $s $d $Variant $Arrows
    $f = Join-Path $OutDir ("timeback-{0}.png" -f $s)
    $b.Save($f,[System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    Write-Host ("  {0,4}px  {1,-7} -> {2}" -f $s,$d,(Split-Path $f -Leaf))
}

Write-Host '--- ICO ---'
$entries = @()
foreach ($s in $icoOrder) {
    $d = Detail-For $s
    $b = Draw-Icon $s $d $Variant $Arrows
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
    $entries += ,@{Size=$s; Bytes=$ms.ToArray()}
    $ms.Dispose(); $b.Dispose()
}

$icoPath = Join-Path $OutDir 'timeback.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$entries.Count)
$offset = 6 + 16*$entries.Count
foreach ($e in $entries) {
    $dim = $e.Size
    if ($dim -ge 256) { $dim = 0 }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$e.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Host ("  timeback.ico ({0} sizes)" -f $entries.Count)
Write-Host ''
Write-Host "output: $OutDir"
