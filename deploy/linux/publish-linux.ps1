$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
$out = Join-Path $root 'publish\linux-x64'
Push-Location $root
try {
    docker build -f deploy/linux/Dockerfile --target export --output "type=local,dest=$out" .
    if ($LASTEXITCODE -ne 0) { throw "docker build that bai" }
    Write-Host "Xong: $out"
} finally { Pop-Location }
