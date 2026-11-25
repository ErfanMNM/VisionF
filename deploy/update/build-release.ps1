#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SigningKeyPath,
    [Parameter(Mandatory)][string]$NotesPath,
    [string]$OutputRoot = 'publish/updates'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'UpdateRelease.psm1') -Force -DisableNameChecking
Assert-ReleaseVersion $Version
if (-not $IsWindows) { throw 'Build hai bản Native AOT bằng script này trên Windows x64 và Docker Linux.' }
foreach ($command in @('git', 'dotnet', 'docker')) { $null = Get-Command $command -ErrorAction Stop }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$keyPath = [IO.Path]::GetFullPath($SigningKeyPath)
$keyPrefix = [IO.Path]::TrimEndingDirectorySeparator($root) + [IO.Path]::DirectorySeparatorChar
if ($keyPath.StartsWith($keyPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Giữ khóa ký ở ngoài repository để khóa không lọt vào Git hoặc Docker build context.'
}
$notes = [IO.File]::ReadAllText([IO.Path]::GetFullPath($NotesPath))
if ([Text.Encoding]::UTF8.GetByteCount($notes) -gt 8KB) { throw 'Ghi chú phát hành vượt giới hạn 8 KiB.' }
if (-not [IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot = Join-Path $root $OutputRoot }
$releaseDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $Version
if (Test-Path -LiteralPath $releaseDirectory) { throw "Thư mục đích đã tồn tại: $releaseDirectory" }

Push-Location $root
$tempParent = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
$temp = Join-Path $tempParent ('tantien-release-' + [Guid]::NewGuid().ToString('N'))
$signingKey = [Security.Cryptography.RSA]::Create()
try {
    $dirty = @(git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) { throw 'Repository chưa sạch. Commit toàn bộ mã nguồn trước khi phát hành.' }
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Không đọc được Git HEAD.' }
    $tagCommit = git rev-parse --verify "refs/tags/v${Version}^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $commit) { throw "HEAD phải trùng với Git tag v$Version." }
    Invoke-ReleaseCommand -Command 'docker' -Arguments @('info', '--format', '{{.OSType}}')

    # Chỉ snapshot Git tag đi vào build, không sao chép secrets hoặc thư mục publish cũ.
    $null = [IO.Directory]::CreateDirectory($temp)
    $snapshot = Join-Path $temp 'source.zip'
    Invoke-ReleaseCommand -Command 'git' -Arguments @('archive', '--format=zip', "--output=$snapshot", "v$Version")
    $source = Join-Path $temp 'source'
    Expand-Archive -LiteralPath $snapshot -DestinationPath $source
    $signingKey.ImportFromPem([IO.File]::ReadAllText($keyPath))
    if ($signingKey.KeySize -lt 3072) { throw 'Khóa RSA dành riêng cho cập nhật phải dài ít nhất 3072 bit.' }
    $publicKey = [Convert]::ToBase64String($signingKey.ExportSubjectPublicKeyInfo())
    $build = Join-Path $temp 'build'
    $winApp = Join-Path $build 'win-x64/app'
    $winLauncher = Join-Path $build 'win-x64/launcher'
    $winHelper = Join-Path $build 'win-x64/printer-remote'
    $linux = Join-Path $build 'linux-x64'
    Push-Location $source
    try {
        $common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", "-p:UpdatePublicKey=$publicKey", '-p:SkipPrinterRemoteHelper=true')
        Invoke-ReleaseCommand -Command 'dotnet' -Arguments (@('publish', 'TanTienQR/TanTienQR.csproj') + $common + @('-o', $winApp))
        Invoke-ReleaseCommand -Command 'dotnet' -Arguments (@('publish', 'tools/TanTienLauncher/TanTienLauncher.csproj') + $common + @('-o', $winLauncher))
        # Helper COM/WinForms chạy riêng ngoài app AOT. Đóng toàn bộ runtime vào
        # một EXE để máy khách không cần cài .NET và manifest vẫn dùng tệp gốc.
        Invoke-ReleaseCommand -Command 'dotnet' -Arguments (@('publish', 'tools/PrinterRemoteHost/PrinterRemoteHost.csproj') + $common + @(
            '--self-contained', 'true', '-p:PublishAot=false', '-p:PublishTrimmed=false',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-o', $winHelper))
        Copy-Item -LiteralPath (Join-Path $winHelper 'TanTien.PrinterRemoteHost.exe') -Destination $winApp
        Invoke-ReleaseCommand -Command 'docker' -Arguments @('build', '-f', 'deploy/update/Dockerfile',
            '--target', 'export', '--build-arg', "RELEASE_VERSION=$Version", '--build-arg', "UPDATE_PUBLIC_KEY=$publicKey",
            '--output', "type=local,dest=$linux", '.')
    }
    finally { Pop-Location }

    foreach ($rid in @('win-x64', 'linux-x64')) {
        $ridBuild = Join-Path $build $rid
        $ridOutput = Join-Path $releaseDirectory $rid
        New-ReleasePackage -Version $Version -Rid $rid -AppDirectory (Join-Path $ridBuild 'app') `
            -LauncherDirectory (Join-Path $ridBuild 'launcher') -OutputDirectory $ridOutput -Notes $notes -SigningKey $signingKey
        Test-ReleaseArtifacts -Directory $ridOutput -Version $Version -Rid $rid -PublicKey $signingKey
    }
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'public-key.spki'), $publicKey)
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'release.json'), ([ordered]@{
        version = $Version
        tag = "v$Version"
        commit = $commit
        builtUtc = [DateTime]::UtcNow.ToString('O')
        bootstrapSha256 = [ordered]@{
            'win-x64' = Get-ReleaseHash (Join-Path $releaseDirectory 'win-x64/bootstrap.zip')
            'linux-x64' = Get-ReleaseHash (Join-Path $releaseDirectory 'linux-x64/bootstrap.zip')
        }
    } | ConvertTo-Json -Depth 4))
    Write-Host "Đã build và kiểm tra cả hai nền tảng: $releaseDirectory"
    Write-Host 'Chưa tải gói lên Cloudflare và chưa công bố kênh.'
}
finally {
    $signingKey.Dispose()
    Pop-Location
    Remove-ReleaseTempDirectory -Directory $temp -Parent $tempParent -Prefix 'tantien-release-'
}
