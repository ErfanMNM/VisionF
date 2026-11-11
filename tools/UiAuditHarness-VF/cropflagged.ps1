param(
  [Parameter(Mandatory=$true)][string]$Tree,
  [Parameter(Mandatory=$true)][string]$Png,
  [Parameter(Mandatory=$true)][string]$OutDir,
  [string]$Match = "",
  [int]$Max = 8,
  [double]$Scale = 5.0,
  [int]$Pad = 8
)

# Crop chính xác quanh các element bị gắn cờ, dựa trên tọa độ đã đo trong
# visual-tree dump. Tránh đoán tọa độ bằng mắt.

Add-Type -AssemblyName System.Drawing

$json = Get-Content $Tree -Raw | ConvertFrom-Json
$nodes = $json.nodes | Where-Object { $_.IsVisible -and $_.W -gt 0 -and $_.H -gt 0 }
if ($Match -ne "") {
  $nodes = $nodes | Where-Object { $_.Path -like "*$Match*" -or $_.Text -like "*$Match*" }
}

$img = [System.Drawing.Image]::FromFile($Png)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$i = 0
foreach ($n in $nodes) {
  if ($i -ge $Max) { break }
  $x = [int][Math]::Max(0, $n.X - $Pad)
  $y = [int][Math]::Max(0, $n.Y - $Pad)
  $w = [int][Math]::Min($img.Width - $x, $n.W + 2 * $Pad)
  $h = [int][Math]::Min($img.Height - $y, $n.H + 2 * $Pad)
  if ($w -le 2 -or $h -le 2) { continue }

  $crop = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($crop)
  $g.DrawImage($img,
    (New-Object System.Drawing.Rectangle 0, 0, $w, $h),
    (New-Object System.Drawing.Rectangle $x, $y, $w, $h),
    [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose()

  $ow = [int]($w * $Scale)
  $oh = [int]($h * $Scale)
  if ($ow -gt 1600) { $oh = [int]($oh * 1600 / $ow); $ow = 1600 }
  $big = New-Object System.Drawing.Bitmap $ow, $oh
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g2.DrawImage($crop, 0, 0, $ow, $oh)
  $g2.Dispose()

  $safe = ($n.Path -replace '[^A-Za-z0-9_\-]', '_')
  if ($safe.Length -gt  70) { $safe = $safe.Substring($safe.Length - 70) }
  $name = "{0:D2}_{1}_{2}x{3}.png" -f $i, $safe, $w, $h
  $big.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $big.Dispose(); $crop.Dispose()

  Write-Output "$name  <- $($n.Type) '$($n.Text)' at ($($n.X),$($n.Y)) ${w}x${h}"
  $i++
}

$img.Dispose()
