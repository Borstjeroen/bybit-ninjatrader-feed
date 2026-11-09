# Optional: regenerates the icons in the folder of this script (the repository already contains them):
#   BybitBridge.ico (orange border)  - the bridge
#   AddOn_on.ico    (green border)   - switch AddOn ON
#   AddOn_off.ico   (red border)     - switch AddOn OFF
Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function RoundRect($x,$y,$w,$h,$r){
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x,$y,$r,$r,180,90); $p.AddArc($x+$w-$r,$y,$r,$r,270,90)
  $p.AddArc($x+$w-$r,$y+$h-$r,$r,$r,0,90); $p.AddArc($x,$y+$h-$r,$r,$r,90,90); $p.CloseFigure(); return $p
}

function MakeIcon([string]$name, [System.Drawing.Color]$border, [string]$symbol) {
  $size = 256
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $bg = RoundRect 8 8 240 240 56
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,20,22,28))), $bg)
  $g.DrawPath((New-Object System.Drawing.Pen $border, 10), $bg)
  $green = [System.Drawing.Color]::FromArgb(255,32,200,100)
  $red   = [System.Drawing.Color]::FromArgb(255,235,60,70)
  $candles = @(@(78,70,100,150,190,$red), @(128,56,80,140,176,$green), @(178,90,108,170,200,$green))
  foreach($c in $candles){
    $x=$c[0]; $col=$c[5]
    $g.DrawLine((New-Object System.Drawing.Pen $col, 8), $x, $c[1], $x, $c[4])
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $col), $x-20, $c[2], 40, ($c[3]-$c[2]))
  }
  if ($symbol -eq 'on') {              # green check mark, bottom right
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,32,200,100)), 14
    $pen.StartCap='Round'; $pen.EndCap='Round'
    $g.DrawLines($pen, @((New-Object System.Drawing.Point 150,205),(New-Object System.Drawing.Point 180,232),(New-Object System.Drawing.Point 232,170)))
  } elseif ($symbol -eq 'off') {       # red pause bars, bottom right
    $br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,235,60,70))
    $g.FillRectangle($br, 170, 168, 20, 62); $g.FillRectangle($br, 204, 168, 20, 62)
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $png = $ms.ToArray()
  $fs = [System.IO.File]::Create((Join-Path $dir $name))
  $bw = New-Object System.IO.BinaryWriter $fs
  $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$png.Length); $bw.Write([UInt32]22)
  $bw.Write($png); $bw.Close(); $fs.Close()
  $bmp.Dispose()
  Write-Host "Icon created: $name"
}

MakeIcon 'BybitBridge.ico' ([System.Drawing.Color]::FromArgb(255,247,166,0)) ''
MakeIcon 'AddOn_on.ico'    ([System.Drawing.Color]::FromArgb(255,32,200,100)) 'on'
MakeIcon 'AddOn_off.ico'   ([System.Drawing.Color]::FromArgb(255,235,60,70))  'off'
