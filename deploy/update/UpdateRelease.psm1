#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-ReleaseCommand {
    param([string]$Command, [string[]]$Arguments)

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Lệnh $Command thất bại (mã thoát $LASTEXITCODE)."
    }
}

function Remove-ReleaseTempDirectory {
    param([string]$Directory, [string]$Parent, [string]$Prefix)

    $resolved = [IO.Path]::GetFullPath($Directory)
    $expectedParent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent))
    if ([IO.Path]::GetDirectoryName($resolved) -cne $expectedParent -or
        -not [IO.Path]::GetFileName($resolved).StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw 'Đường dẫn dọn dẹp nằm ngoài thư mục tạm của bản phát hành.'
    }
    if ([IO.Directory]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Assert-ReleaseVersion {
    param([string]$Version)

    if ($Version -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z' -or
        @($Version.Split('.') | Where-Object { [int]$_ -gt 65534 }).Count -gt 0) {
        throw 'Phiên bản phải có dạng X.Y.Z, mỗi số nằm trong khoảng 0–65534.'
    }
}

function Get-ReleaseHash {
    param([string]$Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-ReleaseZip {
    param([string]$Output, [System.Collections.IDictionary]$Sources, [string[]]$Executables)

    $archive = [IO.Compression.ZipFile]::Open($Output, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Sources.Keys) {
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            # Tệp thường 0644 hoặc 0755 khi giải nén trên Linux.
            $entry.ExternalAttributes = (((0x81A4, 0x81ED)[[int]($name -cin $Executables)]) -shl 16)
            $inputFile = [IO.File]::OpenRead($Sources[$name])
            $outputFile = $entry.Open()
            try { $inputFile.CopyTo($outputFile) }
            finally { $outputFile.Dispose(); $inputFile.Dispose() }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-ReleasePackage {
    param(
        [string]$Version,
        [ValidateSet('win-x64', 'linux-x64')][string]$Rid,
        [string]$AppDirectory,
        [string]$LauncherDirectory,
        [string]$OutputDirectory,
        [AllowEmptyString()][string]$Notes,
        [Security.Cryptography.RSA]$SigningKey
    )

    Assert-ReleaseVersion $Version
    $isLinuxPackage = $Rid -eq 'linux-x64'
    $main = if ($isLinuxPackage) { 'TanTienQR' } else { 'TanTienQR.exe' }
    $launcher = if ($isLinuxPackage) { 'TanTienLauncher' } else { 'TanTienLauncher.exe' }
    $libraryPattern = if ($isLinuxPackage) { '\.so(\.[0-9]+)*\z' } else { '\.dll\z' }
    $sources = [ordered]@{}
    $files = @(
        Get-ChildItem -LiteralPath $AppDirectory -File -Force | Sort-Object -Property Name | ForEach-Object {
            if ($_.Name -ceq $main -or $_.Name -match $libraryPattern) {
                if ($_.Name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*\z' -or
                    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Length -lt 1) {
                    throw "Không thể đóng gói tệp '$($_.Name)'."
                }
                $sources.Add($_.Name, $_.FullName)
                [ordered]@{
                    path = $_.Name
                    sha256 = Get-ReleaseHash $_.FullName
                    size = $_.Length
                    executable = $isLinuxPackage -and $_.Name -ceq $main
                }
            }
        }
    )
    $subdirectories = @(Get-ChildItem -LiteralPath $AppDirectory -Directory -Force)
    if ($subdirectories.Count -gt 0) {
        throw "Thư mục publish $Rid chứa thư mục con. Kiểm tra các tệp runtime trước khi cập nhật quy tắc đóng gói."
    }
    $unpackedSize = 0L
    foreach ($file in $files) { $unpackedSize += [long]$file.size }
    if (-not $sources.Contains($main) -or $files.Count -gt 128 -or $unpackedSize -gt 512MB) {
        throw "Bộ tệp runtime $Rid thiếu chương trình chính hoặc vượt giới hạn."
    }
    $launcherFile = Get-Item -LiteralPath (Join-Path $LauncherDirectory $launcher)
    if (($launcherFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $launcherFile.Length -lt 1) {
        throw "Trình khởi chạy $Rid không hợp lệ."
    }

    $null = [IO.Directory]::CreateDirectory($OutputDirectory)
    $zipPath = Join-Path $OutputDirectory 'package.zip'
    $executables = @()
    if ($isLinuxPackage) { $executables = @($main) }
    New-ReleaseZip -Output $zipPath -Sources $sources -Executables $executables
    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    if ($zipSize -gt 256MB) { throw "Gói $Rid vượt giới hạn 256 MiB." }
    $payload = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
        version = $Version
        rid = $Rid
        size = $zipSize
        sha256 = Get-ReleaseHash $zipPath
        notes = $Notes
        files = $files
    } | ConvertTo-Json -Depth 5 -Compress))
    $signature = $SigningKey.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $envelope = [ordered]@{
        data = [Convert]::ToBase64String($payload)
        sig = [Convert]::ToBase64String($signature)
    } | ConvertTo-Json -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($envelope) -gt 64KB) { throw "Manifest $Rid vượt giới hạn 64 KiB." }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), $envelope)

    # Chỉ bộ cài lần đầu chứa trình khởi chạy. Gói tự cập nhật chỉ thay runtime.
    $sources.Add($launcher, $launcherFile.FullName)
    if ($isLinuxPackage) { $executables += $launcher }
    New-ReleaseZip -Output (Join-Path $OutputDirectory 'bootstrap.zip') -Sources $sources -Executables $executables
}

function Test-ReleaseArtifacts {
    param([string]$Directory, [string]$Version, [string]$Rid, [Security.Cryptography.RSA]$PublicKey)

    $manifest = Join-Path $Directory 'manifest.json'
    $zipPath = Join-Path $Directory 'package.zip'
    if ((Get-Item -LiteralPath $manifest).Length -gt 64KB) { throw "Manifest $Rid vượt giới hạn 64 KiB." }
    $envelope = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
    $payload = [Convert]::FromBase64String($envelope.data)
    $signature = [Convert]::FromBase64String($envelope.sig)
    if (-not $PublicKey.VerifyData($payload, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw "Chữ ký manifest $Rid không hợp lệ."
    }
    $release = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json -AsHashtable
    if ($release.version -cne $Version -or $release.rid -cne $Rid -or $release.size -lt 1 -or
        $release.size -gt 256MB -or $release.size -ne (Get-Item -LiteralPath $zipPath).Length -or
        $release.sha256 -cne (Get-ReleaseHash $zipPath)) {
        throw "Sai phiên bản, kiến trúc, kích thước hoặc SHA-256 của gói $Rid."
    }
}

Export-ModuleMember -Function Invoke-ReleaseCommand, Remove-ReleaseTempDirectory, Assert-ReleaseVersion,
    Get-ReleaseHash, New-ReleasePackage, Test-ReleaseArtifacts
