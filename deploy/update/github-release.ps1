#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Upload', 'Promote', 'Withdraw')][string]$Action,
    [Parameter(Mandatory)][string]$Version,
    [string]$ReleaseDirectory,
    [string]$PublicKeyPath,
    [string]$NotesPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'UpdateRelease.psm1') -Force -DisableNameChecking
Assert-ReleaseVersion $Version

$repository = 'ErfanMNM/VisionF'
$tag = "v$Version"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$null = Get-Command gh -ErrorAction Stop
$null = Get-Command git -ErrorAction Stop

function Invoke-External {
    param([string]$Command, [string[]]$Arguments, [string]$Purpose)

    $output = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "$Purpose thất bại (mã thoát $exitCode)." }
    return $output
}

function Invoke-GhApi {
    param([string[]]$Arguments, [string]$Purpose, [switch]$AllowNotFound)

    $output = @(& gh api @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $responseText = $output -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        if ($AllowNotFound -and $responseText -match '(?i)404') { return $null }
        throw "$Purpose thất bại (mã thoát $exitCode)."
    }
    if ([string]::IsNullOrWhiteSpace($responseText)) { return $null }
    return ,(ConvertFrom-Json -InputObject $responseText -AsHashtable -NoEnumerate)
}

function Get-Release {
    param([string]$ReleaseTag)

    $published = Invoke-GhApi -Arguments @("repos/$repository/releases/tags/$ReleaseTag") `
        -Purpose 'Đọc GitHub Release' -AllowNotFound
    if ($null -ne $published) { return $published }

    # The REST tag endpoint only resolves published releases. A push-authorized
    # `gh` session sees drafts in the release list, so use it to resume/withdraw drafts.
    $releases = @(Get-GitHubReleaseList)
    return @($releases | Where-Object { $_.draft -and $_.tag_name -ceq $ReleaseTag } | Select-Object -First 1)
}

function Set-ReleaseState {
    param([long]$ReleaseId, [bool]$Draft, [bool]$Prerelease)

    $null = Invoke-GhApi -Arguments @('-X', 'PATCH', "repos/$repository/releases/$ReleaseId",
        '-F', "draft=$($Draft.ToString().ToLowerInvariant())",
        '-F', "prerelease=$($Prerelease.ToString().ToLowerInvariant())") `
        -Purpose 'Cập nhật trạng thái GitHub Release'
}

function Assert-PublicRepository {
    $response = Invoke-External -Command 'gh' -Arguments @('repo', 'view', $repository, '--json', 'nameWithOwner,isPrivate') `
        -Purpose 'Kiểm tra repository GitHub'
    $repoInfo = ($response -join [Environment]::NewLine) | ConvertFrom-Json
    if ($repoInfo.nameWithOwner -cne $repository -or $repoInfo.isPrivate) {
        throw 'Repository phải là ErfanMNM/VisionF công khai để Worker tải asset mà không cần token.'
    }
}

function Assert-TagIsPublishedAtHead {
    $localCommit = @(& git -C $repoRoot rev-parse --verify "refs/tags/${tag}^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $localCommit.Count -ne 1) { throw "Thiếu tag cục bộ $tag." }
    $head = @(& git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $localCommit[0].Trim() -cne $head[0].Trim()) {
        throw "Tag $tag phải trỏ đúng HEAD hiện tại."
    }
    $status = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $status.Count -gt 0) { throw 'Working tree phải sạch trước khi phát hành.' }

    $remoteLines = @(& git -C $repoRoot ls-remote --tags origin "refs/tags/$tag" "refs/tags/${tag}^{}")
    if ($LASTEXITCODE -ne 0 -or $remoteLines.Count -eq 0) { throw "Tag $tag cần được đẩy lên origin trước khi phát hành." }
    $remoteCommit = $null
    foreach ($line in $remoteLines) {
        if ($line -match "^([0-9a-fA-F]{40,64})\s+refs/tags/$([regex]::Escape($tag))(\^\{\})?$") {
            if ($Matches[2]) { $remoteCommit = $Matches[1]; break }
            if (-not $remoteCommit) { $remoteCommit = $Matches[1] }
        }
    }
    if (-not $remoteCommit -or $remoteCommit -cne $localCommit[0].Trim()) {
        throw "Tag từ xa $tag không trùng commit cục bộ."
    }
}

function Get-GitHubReleaseList {
    $result = Invoke-GhApi -Arguments @("repos/$repository/releases?per_page=100") -Purpose 'Đọc danh sách GitHub Releases'
    if ($result -isnot [array]) { throw 'GitHub Releases trả về danh sách không hợp lệ.' }
    return $result
}

function Get-LatestChannelVersion {
    param([array]$Releases, [bool]$Prerelease)

    $versions = @(
        foreach ($item in $Releases) {
            if ($item.draft -or [bool]$item.prerelease -ne $Prerelease) { continue }
            if ($item.tag_name -cmatch '^v(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
                $candidate = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
                try { [version]$candidate } catch { }
            }
        }
    )
    if ($versions.Count -eq 0) { return $null }
    return [string](($versions | ForEach-Object { [version]$_ } | Sort-Object -Descending | Select-Object -First 1).ToString(3))
}

function Get-LocalAssets {
    param([string]$Directory, [string]$KeyPath)

    $resolvedDirectory = (Resolve-Path -LiteralPath $Directory).Path
    $resolvedKeyPath = (Resolve-Path -LiteralPath $KeyPath).Path
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $publicKey = [IO.File]::ReadAllText($resolvedKeyPath).Trim()
        $rsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($publicKey), [ref]$null)
        if ($rsa.KeySize -lt 3072) { throw 'Khóa xác minh RSA phải dài ít nhất 3072 bit.' }

        $metadataPath = Join-Path $resolvedDirectory 'release.json'
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'Thiếu release.json.' }
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -AsHashtable
        if ($metadata.version -cne $Version -or $metadata.tag -cne $tag) {
            throw 'Thông tin release.json không khớp phiên bản/tag yêu cầu.'
        }
        $tagCommit = @(& git -C $repoRoot rev-parse --verify "refs/tags/${tag}^{commit}" 2>$null)
        if ($LASTEXITCODE -ne 0 -or $tagCommit.Count -ne 1 -or $metadata.commit -cne $tagCommit[0].Trim()) {
            throw 'Commit trong release.json phải khớp Git tag.'
        }
        $bundledKeyPath = Join-Path $resolvedDirectory 'public-key.spki'
        if (-not (Test-Path -LiteralPath $bundledKeyPath -PathType Leaf) -or
            [IO.File]::ReadAllText($bundledKeyPath).Trim() -cne $publicKey) {
            throw 'public-key.spki phải khớp khóa xác minh dùng cho manifest.'
        }

        $assets = [Collections.Generic.List[object]]::new()
        foreach ($rid in @('win-x64', 'linux-x64')) {
            $platformDirectory = Join-Path $resolvedDirectory $rid
            Test-ReleaseArtifacts -Directory $platformDirectory -Version $Version -Rid $rid -PublicKey $rsa
            foreach ($name in @('manifest.json', 'package.zip', 'bootstrap.zip')) {
                $source = Join-Path $platformDirectory $name
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Thiếu $rid/$name." }
                if ((Get-Item -LiteralPath $source).Length -lt 1) { throw "Tệp $rid/$name rỗng." }
            }

            $bootstrapPath = Join-Path $platformDirectory 'bootstrap.zip'
            $bootstrapHash = Get-ReleaseHash $bootstrapPath
            $expectedBootstrapHash = $metadata.bootstrapSha256
            if ($expectedBootstrapHash -is [System.Collections.IDictionary]) {
                $expectedBootstrapHash = $expectedBootstrapHash[$rid]
            }
            if (-not $expectedBootstrapHash -or $expectedBootstrapHash -cne $bootstrapHash) {
                throw "SHA-256 bootstrap không khớp release.json cho $rid."
            }

            foreach ($kind in @('manifest', 'package', 'bootstrap')) {
                $sourceName = switch ($kind) {
                    'manifest' { 'manifest.json' }
                    'package' { 'package.zip' }
                    'bootstrap' { 'bootstrap.zip' }
                }
                $source = Join-Path $platformDirectory $sourceName
                $assetName = if ($kind -eq 'manifest') { "manifest-$rid.json" }
                    elseif ($kind -eq 'package') { "package-$rid.zip" }
                    else { "bootstrap-$rid.zip" }
                $assets.Add([pscustomobject]@{ Name = $assetName; Path = $source; Size = (Get-Item -LiteralPath $source).Length; Sha256 = Get-ReleaseHash $source })
            }
        }

        foreach ($name in @('release.json', 'public-key.spki')) {
            $path = Join-Path $resolvedDirectory $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Thiếu $name." }
            $assets.Add([pscustomobject]@{ Name = $name; Path = $path; Size = (Get-Item -LiteralPath $path).Length; Sha256 = Get-ReleaseHash $path })
        }
        return $assets.ToArray()
    }
    finally { $rsa.Dispose() }
}

function Assert-RemoteAssets {
    param([System.Collections.IDictionary]$Release, [object[]]$ExpectedAssets, [switch]$RequireAll)

    $remoteByName = @{}
    foreach ($asset in @($Release.assets)) { $remoteByName[$asset.name] = $asset }
    foreach ($expected in $ExpectedAssets) {
        if (-not $remoteByName.ContainsKey($expected.Name)) {
            if ($RequireAll) { throw "GitHub Release thiếu asset $($expected.Name)." }
            continue
        }
        $remote = $remoteByName[$expected.Name]
        if ([long]$remote.size -ne [long]$expected.Size -or
            $remote.digest -cne "sha256:$($expected.Sha256)") {
            throw "Asset GitHub $($expected.Name) không khớp bản cục bộ (kích thước/SHA-256)."
        }
    }
}

function Get-VerifiedReleaseContext {
    if (-not $ReleaseDirectory) { throw "-ReleaseDirectory là bắt buộc cho $Action." }
    if (-not $PublicKeyPath) { $PublicKeyPath = Join-Path $ReleaseDirectory 'public-key.spki' }
    if (-not (Test-Path -LiteralPath $ReleaseDirectory -PathType Container)) { throw "Không tìm thấy thư mục phát hành $ReleaseDirectory." }
    $assets = @(Get-LocalAssets -Directory $ReleaseDirectory -KeyPath $PublicKeyPath)
    Assert-TagIsPublishedAtHead
    return $assets
}

function Start-PilotUpload {
    $assets = @(Get-VerifiedReleaseContext)
    $releases = @(Get-GitHubReleaseList)
    $latestStable = Get-LatestChannelVersion -Releases $releases -Prerelease $false
    $latestPilot = Get-LatestChannelVersion -Releases $releases -Prerelease $true
    if ($latestStable -and [version]$Version -le [version]$latestStable) { throw "Pilot $Version phải mới hơn stable $latestStable." }
    if ($latestPilot -and [version]$Version -le [version]$latestPilot) { throw "Pilot $Version phải mới hơn pilot hiện tại $latestPilot." }

    $release = Get-Release -ReleaseTag $tag
    if ($null -eq $release) {
        $notes = if ($NotesPath) { (Resolve-Path -LiteralPath $NotesPath).Path } else { $null }
        $createArgs = @('release', 'create', $tag, '--repo', $repository, '--verify-tag', '--draft', '--title', "TanTienQR $Version")
        if ($notes) { $createArgs += @('--notes-file', $notes) }
        else { $createArgs += @('--notes', "Bản pilot TanTienQR $Version.") }
        $null = Invoke-External -Command 'gh' -Arguments $createArgs -Purpose 'Tạo GitHub Release nháp'
        $release = Get-Release -ReleaseTag $tag
    }
    if ($null -eq $release -or $release.tag_name -cne $tag) { throw "Không đọc được GitHub Release $tag sau khi tạo." }
    if (-not $release.draft) { throw 'Chỉ có thể tải asset vào Release nháp; không sửa asset đã công bố.' }

    Assert-RemoteAssets -Release $release -ExpectedAssets $assets
    $remoteNames = @($release.assets | ForEach-Object name)
    $missing = @($assets | Where-Object { $remoteNames -notcontains $_.Name })
    if ($missing.Count -gt 0) {
        $tempParent = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
        $temp = Join-Path $tempParent ('tantien-github-assets-' + [Guid]::NewGuid().ToString('N'))
        $null = [IO.Directory]::CreateDirectory($temp)
        try {
            $paths = @()
            foreach ($asset in $missing) {
                $destination = Join-Path $temp $asset.Name
                Copy-Item -LiteralPath $asset.Path -Destination $destination
                $paths += $destination
            }
            $null = Invoke-External -Command 'gh' -Arguments (@('release', 'upload', $tag, '--repo', $repository) + $paths) `
                -Purpose 'Tải asset lên GitHub Release nháp'
        }
        finally { Remove-ReleaseTempDirectory -Directory $temp -Parent $tempParent -Prefix 'tantien-github-assets-' }
    }

    $release = Get-Release -ReleaseTag $tag
    Assert-RemoteAssets -Release $release -ExpectedAssets $assets -RequireAll
    Set-ReleaseState -ReleaseId ([long]$release.id) -Draft $false -Prerelease $true
    $release = Get-Release -ReleaseTag $tag
    if ($release.draft -or -not $release.prerelease) { throw 'GitHub không xác nhận phát hành pilot.' }
    Write-Host "Đã xác minh và công bố pilot $Version trên GitHub Releases. Stable chưa thay đổi."
}

function Promote-Pilot {
    $assets = @(Get-VerifiedReleaseContext)
    $release = Get-Release -ReleaseTag $tag
    if ($null -eq $release -or $release.draft -or -not $release.prerelease) {
        throw "Release $tag phải là pilot đã công bố trước khi promote."
    }
    Assert-RemoteAssets -Release $release -ExpectedAssets $assets -RequireAll
    $releases = @(Get-GitHubReleaseList)
    $latestPilot = Get-LatestChannelVersion -Releases $releases -Prerelease $true
    if ($latestPilot -cne $Version) { throw "Chỉ promote được pilot mới nhất ($latestPilot)." }
    $latestStable = Get-LatestChannelVersion -Releases $releases -Prerelease $false
    if ($latestStable -and [version]$Version -le [version]$latestStable) {
        throw "Stable $Version phải mới hơn stable hiện tại $latestStable."
    }
    Set-ReleaseState -ReleaseId ([long]$release.id) -Draft $false -Prerelease $false
    $release = Get-Release -ReleaseTag $tag
    if ($release.draft -or $release.prerelease) { throw 'GitHub không xác nhận promote sang stable.' }
    Write-Host "Đã promote $Version từ pilot sang stable."
}

function Withdraw-Release {
    $release = Get-Release -ReleaseTag $tag
    if ($null -eq $release -or $release.draft) { throw "Không có release đã công bố $tag để rút." }
    Set-ReleaseState -ReleaseId ([long]$release.id) -Draft $true -Prerelease ([bool]$release.prerelease)
    $release = Get-Release -ReleaseTag $tag
    if (-not $release.draft) { throw 'GitHub không xác nhận rút release khỏi các kênh.' }
    Write-Host "Đã rút $Version khỏi kênh cập nhật bằng cách chuyển release về bản nháp. Asset được giữ để có thể khôi phục."
}

Push-Location $repoRoot
try {
    Assert-PublicRepository
    switch ($Action) {
        'Upload' { Start-PilotUpload }
        'Promote' { Promote-Pilot }
        'Withdraw' { Withdraw-Release }
    }
}
finally { Pop-Location }
