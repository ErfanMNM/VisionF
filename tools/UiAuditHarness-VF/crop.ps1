param(
  [Parameter(Mandatory=$true)][string]$Src,
  [Parameter(Mandatory=$true)][string]$Dest,
  [int]$X = 0,
  [int]$Y = 0,
  [int]$W = 0,
  [int]$H = 0,
  [double]$Scale = 2.0
)

Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Image]::FromFile($Src)
if ($W -le 0) { $W = $img.Width - $X }
if ($H -le 0) { $H = $img.Height - $Y }

$crop = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.DrawImage($img,
  (New-Object System.Drawing.Rectangle 0, 0, $W, $H),
  (New-Object System.Drawing.Rectangle $X, $Y, $W, $H),
  [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

$ow = [int]($W * $Scale)
$oh = [int]($H * $Scale)
$big = New-Object System.Drawing.Bitmap $ow, $oh
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($crop, 0, 0, $ow, $oh)
$g2.Dispose()
$big.Save($Dest, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
$crop.Dispose()
$img.Dispose()

Write-Output "saved $Dest ($ow x $oh)"
