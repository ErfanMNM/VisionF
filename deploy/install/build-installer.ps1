#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$OutputDirectory,
    [string]$InnoCompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../update/UpdateRelease.psm1') -Force -DisableNameChecking

$ReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$release = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'release.json') -Raw | ConvertFrom-Json -AsHashtable
$version = [string]$release.version
Assert-ReleaseVersion $version
if ($release.tag -cne "v$version" -or $release.bootstrapSha256.'win-x64' -notmatch '^[0-9a-f]{64}$') {
    throw 'Metadata release không hợp lệ.'
}

$ridDirectory = Join-Path $ReleaseDirectory 'win-x64'
$bootstrap = Join-Path $ridDirectory 'bootstrap.zip'
if (-not (Test-Path -LiteralPath $bootstrap -PathType Leaf) -or
    (Get-ReleaseHash $bootstrap) -cne $release.bootstrapSha256.'win-x64') {
    throw 'SHA-256 bootstrap win-x64 không khớp release.json.'
}

$requiredFiles = @('TanTienQR.exe', 'TanTienLauncher.exe', 'TanTien.PrinterRemoteHost.exe')
$temporaryRoot = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
$stagingParent = Join-Path $temporaryRoot ('tantien-installer-' + [Guid]::NewGuid().ToString('N'))
$stagingDirectory = Join-Path $stagingParent 'payload'
$signingKeyNames = @('*.key', '*.pem', '*private*')
$setupName = "TanTienQR-$version-win-x64-setup.exe"
$publicKeyName = "TanTienQR-$version-public-key.spki"

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $ReleaseDirectory 'installer' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$setupPath = Join-Path $OutputDirectory $setupName

try {
    if (Test-Path -LiteralPath $setupPath) { throw "Installer đầu ra đã tồn tại: $setupPath" }
    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        $null = New-Item -ItemType Directory -Path $OutputDirectory
    }
    $null = New-Item -ItemType Directory -Path $stagingDirectory
    Expand-Archive -LiteralPath $bootstrap -DestinationPath $stagingDirectory

    $payload = @(Get-ChildItem -LiteralPath $stagingDirectory -File -Force)
    if ($payload.Count -lt $requiredFiles.Count) { throw 'Bootstrap thiếu tệp chương trình phát hành.' }
    foreach ($name in $requiredFiles) {
        $file = Get-Item -LiteralPath (Join-Path $stagingDirectory $name) -ErrorAction SilentlyContinue
        if (-not $file -or $file.Length -lt 1 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Bootstrap thiếu hoặc có tệp không hợp lệ: $name"
        }
    }
    $forbidden = @($payload | Where-Object {
        $_.Name -match '^(appsettings(\..+)?\.json|license\.lic|.*\.(db|pdb|key|pem))$' -or
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($_.Name -in $signingKeyNames)
    })
    if ($forbidden.Count -gt 0) { throw 'Bootstrap có cấu hình riêng, database, symbols hoặc khóa ký; dừng đóng gói.' }
    if (@(Get-ChildItem -LiteralPath $stagingDirectory -Directory -Force).Count -gt 0) {
        throw 'Bootstrap chứa thư mục con ngoài contract đóng gói.'
    }

    if (-not $InnoCompilerPath) {
        $compilerCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if (-not $compilerCommand) { throw 'Cần cài Inno Setup 6/7 hoặc truyền -InnoCompilerPath trỏ tới ISCC.exe.' }
        $InnoCompilerPath = $compilerCommand.Source
    }
    $compiler = (Resolve-Path -LiteralPath $InnoCompilerPath).Path
    $iconPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../TanTienQR/Assets/tantien-logo.ico')).Path
    $scriptPath = Join-Path $PSScriptRoot 'TanTienQR.iss'
    $processInfo = [Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $compiler
    $processInfo.UseShellExecute = $false
    foreach ($argument in @(
        "-DAppVersion=$version",
        "-DStagingDirectory=$stagingDirectory",
        "-DOutputDirectory=$OutputDirectory",
        "-DIconPath=$iconPath",
        $scriptPath
    )) { $processInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($processInfo)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "ISCC thất bại hoặc không tạo installer (mã thoát $($process.ExitCode))."
    }

    $checksum = "$(Get-ReleaseHash $setupPath)  $setupName"
    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$setupName.sha256"), $checksum, [Text.UTF8Encoding]::new($false))
    Write-Host "Đã đóng gói installer và xác minh bootstrap SHA-256: $setupPath"
}
finally {
    Remove-ReleaseTempDirectory -Directory $stagingParent -Parent $temporaryRoot -Prefix 'tantien-installer-'
}
