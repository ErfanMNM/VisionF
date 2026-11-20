#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Upload', 'Promote', 'Withdraw')][string]$Action,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{32}$')][string]$AccountId,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$')][string]$Bucket,
    [string]$ReleaseDirectory,
    [string]$PublicKeyPath,
    [ValidateSet('pilot', 'stable')][string]$Channel = 'stable',
    [switch]$PilotVerified
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'UpdateRelease.psm1') -Force -DisableNameChecking
Assert-ReleaseVersion $Version
$null = Get-Command aws -ErrorAction Stop
$endpoint = "https://$AccountId.r2.cloudflarestorage.com"
$tempParent = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
$temp = Join-Path $tempParent ('tantien-r2-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temp)
$publicKey = [Security.Cryptography.RSA]::Create()

function Invoke-R2 {
    param([string[]]$Arguments, [switch]$AllowMissing)

    $response = @(& aws s3api @Arguments --endpoint-url $endpoint --region auto --no-cli-pager 2>&1)
    $exitCode = $LASTEXITCODE
    $responseText = $response -join "`n"
    if ($exitCode -ne 0) {
        if ($AllowMissing -and $responseText -match '\((NoSuchKey|404|NotFound)\)') { return $null }
        throw "Thao tác R2 thất bại (mã thoát $exitCode). $responseText"
    }
    if ($responseText.Trim()) { return $responseText | ConvertFrom-Json -AsHashtable }
    return @{}
}

function Read-R2Object {
    param([string]$Key, [string]$Destination, [switch]$AllowMissing)

    Invoke-R2 -Arguments @('get-object', '--bucket', $Bucket, '--key', $Key, $Destination) -AllowMissing:$AllowMissing
}

function Write-ImmutableR2Object {
    param([string]$Key, [string]$Source, [string]$ContentType)

    $download = Join-Path $temp ([Guid]::NewGuid().ToString('N'))
    $existing = Read-R2Object -Key $Key -Destination $download -AllowMissing
    if ($null -eq $existing) {
        $null = Invoke-R2 -Arguments @('put-object', '--bucket', $Bucket, '--key', $Key, '--body', $Source,
            '--content-type', $ContentType, '--cache-control', 'private, no-store', '--if-none-match', '*')
    }
    elseif ((Get-ReleaseHash $Source) -cne (Get-ReleaseHash $download)) {
        throw "Đã tồn tại nội dung khác tại '$Key'. Dùng số phiên bản mới, không ghi đè bản phát hành."
    }
}

function Assert-RemoteArtifacts {
    foreach ($rid in @('win-x64', 'linux-x64')) {
        $directory = Join-Path $temp $rid
        $null = [IO.Directory]::CreateDirectory($directory)
        foreach ($file in @('manifest.json', 'package.zip')) {
            $null = Read-R2Object -Key "releases/$Version/$rid/$file" -Destination (Join-Path $directory $file)
        }
        Test-ReleaseArtifacts -Directory $directory -Version $Version -Rid $rid -PublicKey $publicKey
        foreach ($file in @('manifest.json', 'package.zip')) {
            if ((Get-ReleaseHash (Join-Path $directory $file)) -cne
                (Get-ReleaseHash (Join-Path $ReleaseDirectory "$rid/$file"))) {
                throw "Bản $rid trên R2 không trùng với gói địa phương đã được duyệt."
            }
        }
    }
}

function Write-ChannelPointer {
    param([string]$TargetChannel, [string]$Body, [string]$ExpectedVersion)

    $pointerKey = "channels/$TargetChannel.json"
    $currentPath = Join-Path $temp "$TargetChannel-current.json"
    $current = Read-R2Object -Key $pointerKey -Destination $currentPath -AllowMissing
    if ($ExpectedVersion) {
        if ($null -eq $current) { throw "Kênh $TargetChannel không tồn tại." }
        $pointer = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json -AsHashtable
        if (-not $pointer.ContainsKey('version') -or $pointer.version -cne $ExpectedVersion) {
            throw "Kênh $TargetChannel không còn trỏ tới $ExpectedVersion; dừng để tránh rút nhầm bản."
        }
    }
    $newPath = Join-Path $temp "$TargetChannel-new.json"
    [IO.File]::WriteAllText($newPath, $Body)
    $condition = if ($null -eq $current) { @('--if-none-match', '*') } else { @('--if-match', [string]$current.ETag) }
    $null = Invoke-R2 -Arguments (@('put-object', '--bucket', $Bucket, '--key', $pointerKey, '--body', $newPath,
        '--content-type', 'application/json', '--cache-control', 'private, no-store') + $condition)
}

try {
    if ($Action -eq 'Withdraw') {
        # Ghi con trỏ rỗng có điều kiện để tránh cuộc đua xoá nhầm bản mới.
        Write-ChannelPointer -TargetChannel $Channel -Body '{}' -ExpectedVersion $Version
        Write-Host "Đã rút $Version khỏi kênh $Channel. Các gói và backup tại máy khách được giữ lại."
        return
    }
    if (-not $ReleaseDirectory -or -not $PublicKeyPath) { throw 'Cần ReleaseDirectory và PublicKeyPath để kiểm tra gói.' }
    $ReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
    $keyBase64 = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PublicKeyPath).Path).Trim()
    $publicKey.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($keyBase64), [ref]$null)
    if ($publicKey.KeySize -lt 3072) { throw 'Khóa công khai RSA phải dài ít nhất 3072 bit.' }
    foreach ($rid in @('win-x64', 'linux-x64')) {
        Test-ReleaseArtifacts -Directory (Join-Path $ReleaseDirectory $rid) -Version $Version -Rid $rid -PublicKey $publicKey
    }

    if ($Action -eq 'Upload') {
        foreach ($rid in @('win-x64', 'linux-x64')) {
            $directory = Join-Path $ReleaseDirectory $rid
            Write-ImmutableR2Object -Key "releases/$Version/$rid/package.zip" -Source (Join-Path $directory 'package.zip') -ContentType 'application/zip'
            Write-ImmutableR2Object -Key "releases/$Version/$rid/manifest.json" -Source (Join-Path $directory 'manifest.json') -ContentType 'application/json'
        }
        Assert-RemoteArtifacts
        Write-ChannelPointer -TargetChannel 'pilot' -Body (@{ version = $Version } | ConvertTo-Json -Compress)
        Write-Host "Đã kiểm tra cả hai gói trên R2 và công bố $Version trên pilot. Stable chưa thay đổi."
    }
    else {
        if (-not $PilotVerified) { throw 'Chỉ Promote sau khi đã thử đạt Windows và Linux pilot; ghi nhận bằng -PilotVerified.' }
        $pilotPath = Join-Path $temp 'pilot.json'
        $null = Read-R2Object -Key 'channels/pilot.json' -Destination $pilotPath
        $pilot = Get-Content -LiteralPath $pilotPath -Raw | ConvertFrom-Json -AsHashtable
        if (-not $pilot.ContainsKey('version') -or $pilot.version -cne $Version) { throw "Pilot không trỏ đến $Version." }
        Assert-RemoteArtifacts
        Write-ChannelPointer -TargetChannel 'stable' -Body (@{ version = $Version } | ConvertTo-Json -Compress)
        Write-Host "Đã đưa đúng hai gói pilot $Version lên stable. Không build hoặc ký lại."
    }
}
finally {
    $publicKey.Dispose()
    Remove-ReleaseTempDirectory -Directory $temp -Parent $tempParent -Prefix 'tantien-r2-'
}
