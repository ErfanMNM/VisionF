param(
  [string]$Tree,
  [string]$RootType,
  [switch]$Buttons,
  [switch]$Texts,
  [switch]$Grids,
  [switch]$All
)

$ErrorActionPreference = 'Stop'
$t = Get-Content $Tree -Raw | ConvertFrom-Json
$nodes = $t.nodes

# Build map
$map = @{}
foreach ($n in $nodes) { $map[$n.Path] = $n }

# Find visible root of given type
$root = $nodes | Where-Object { $_.Type -eq $RootType -and $_.IsVisible -eq $true } | Select-Object -First 1
if (-not $root) { Write-Output "ROOT NOT FOUND: $RootType"; exit 1 }

$prefix = $root.Path
$sub = $nodes | Where-Object { $_.Path -eq $prefix -or $_.Path.StartsWith($prefix + '/') }

# Visible-only descendants (child visible AND all ancestors visible)
function Get-Visible($n) {
  $p = $n.Path
  while ($p -and $p -ne '/') {
    if ($map.ContainsKey($p)) {
      $v = $map[$p].IsVisible
      if ($v -eq $false -or $v -eq $null) { return $false }
    }
    $idx = $p.LastIndexOf('/')
    if ($idx -le 0) { break }
    $p = $p.Substring(0, $idx)
  }
  return $true
}

$vis = $sub | Where-Object { (Get-Visible $_) }

if ($All) {
  $vis | ForEach-Object { "{0,-20} x={1,-6} y={2,-6} w={3,-7} h={4,-6} fh={5,-5} fw={6,-5} '{7}'" -f $_.Type,$_.X,$_.Y,$_.W,$_.H,$_.FixedHeight,$_.FixedWidth,$_.Text }
  exit 0
}

if ($Buttons) {
  $vis | Where-Object { $_.Type -match 'Button' } | ForEach-Object {
    "{0,-22} x={1,-6} y={2,-6} w={3,-6} h={4,-5} fh={5,-5} '{6}'" -f $_.Type,$_.X,$_.Y,$_.W,$_.H,$_.FixedHeight,$_.Text
  }
  exit 0
}

if ($Texts) {
  $vis | Where-Object { $_.Type -eq 'TextBlock' -and $_.Text -ne '' } | ForEach-Object {
    "x={0,-6} y={1,-6} w={2,-6} h={3,-5} dW={4,-6} dH={5,-5} fs={6,-5} '{7}'" -f $_.X,$_.Y,$_.W,$_.H,$_.DesiredW,$_.DesiredH,$_.FontSize,$_.Text
  }
  exit 0
}

if ($Grids) {
  $vis | Where-Object { $_.Type -eq 'Grid' } | ForEach-Object {
    "Grid x={0,-6} y={1,-6} w={2,-7} h={3,-6} path={4}" -f $_.X,$_.Y,$_.W,$_.H,($_.Path.Substring($prefix.Length))
  }
  exit 0
}
