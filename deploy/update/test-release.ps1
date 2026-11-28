#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'UpdateRelease.psm1') -Force -DisableNameChecking
$tempParent = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
$temp = Join-Path $tempParent ('tantien-release-test-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temp)
$key = [Security.Cryptography.RSA]::Create(3072)
$passed = 0
$global:UpdateReleaseTestSavedGh = Get-Command gh -CommandType Function -ErrorAction SilentlyContinue
$global:UpdateReleaseTestSavedGit = Get-Command git -CommandType Function -ErrorAction SilentlyContinue
$global:UpdateReleaseTestSavedLastExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$global:UpdateReleaseTestCommit = ''
$global:UpdateReleaseTestReleases = @()
$global:UpdateReleaseTestNextId = 1
$global:UpdateReleaseTestMutations = 0

function Assert-That {
    param([bool]$Condition, [string]$Description)
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:passed++
    Write-Host "PASS: $Description"
}

function Assert-Rejected {
    param([scriptblock]$Operation, [string]$Description)
    $rejected = $false
    try { & $Operation } catch { $rejected = $true }
    Assert-That $rejected $Description
}

try {
    $releaseDirectory = Join-Path $temp '1.2.3'
    foreach ($rid in @('win-x64', 'linux-x64')) {
        $app = Join-Path $temp "$rid/app"
        $launcher = Join-Path $temp "$rid/launcher"
        $output = Join-Path $releaseDirectory $rid
        $null = [IO.Directory]::CreateDirectory($app)
        $null = [IO.Directory]::CreateDirectory($launcher)
        $mainName = if ($rid -eq 'win-x64') { 'TanTienQR.exe' } else { 'TanTienQR' }
        $launcherName = if ($rid -eq 'win-x64') { 'TanTienLauncher.exe' } else { 'TanTienLauncher' }
        $nativeName = if ($rid -eq 'win-x64') { 'e_sqlite3.dll' } else { 'libe_sqlite3.so.1' }
        [IO.File]::WriteAllText((Join-Path $app $mainName), 'runtime-v1.2.3')
        [IO.File]::WriteAllText((Join-Path $app $nativeName), 'native-runtime')
        $helperName = 'TanTien.PrinterRemoteHost.exe'
        if ($rid -eq 'win-x64') {
            [IO.File]::WriteAllText((Join-Path $app $helperName), 'printer-remote-runtime')
        }
        [IO.File]::WriteAllText((Join-Path $app 'appsettings.json'), '{"private":"config"}')
        [IO.File]::WriteAllText((Join-Path $app 'license.lic'), 'private-license')
        [IO.File]::WriteAllText((Join-Path $app 'debug.pdb'), 'private-symbols')
        [IO.File]::WriteAllText((Join-Path $launcher $launcherName), 'stable-launcher')
        New-ReleasePackage -Version '1.2.3' -Rid $rid -AppDirectory $app -LauncherDirectory $launcher `
            -OutputDirectory $output -Notes 'Kiểm thử tiếng Việt' -SigningKey $key
        Test-ReleaseArtifacts -Directory $output -Version '1.2.3' -Rid $rid -PublicKey $key
        Assert-That $true "Chữ ký và SHA-256 gói $rid hợp lệ"
        $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'package.zip'))
        try {
            $expectedCount = if ($rid -eq 'win-x64') { 3 } else { 2 }
            Assert-That ($archive.Entries.Count -eq $expectedCount) "Gói $rid chỉ có runtime, không chứa cấu hình, license, symbols hoặc launcher"
            Assert-That (($null -ne $archive.GetEntry($helperName)) -eq ($rid -eq 'win-x64')) "Helper remote chỉ nằm trong gói Windows"
            if ($rid -eq 'linux-x64') {
                $entry = $archive.GetEntry($mainName)
                Assert-That ((($entry.ExternalAttributes -shr 16) -band 0x1FF) -eq 0x1ED) 'Giữ quyền thực thi 0755 trong ZIP Linux'
            }
        }
        finally { $archive.Dispose() }
        $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'bootstrap.zip'))
        try {
            Assert-That ($null -ne $archive.GetEntry($launcherName)) "Bootstrap $rid có trình khởi chạy"
            Assert-That (($null -ne $archive.GetEntry($helperName)) -eq ($rid -eq 'win-x64')) "Bootstrap $rid mang đúng bộ helper"
        }
        finally { $archive.Dispose() }

        if ($rid -eq 'win-x64') {
            $envelope = Get-Content -LiteralPath (Join-Path $output 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
            $manifest = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.data)) | ConvertFrom-Json -AsHashtable
            $helper = @($manifest.files | Where-Object { $_.path -ceq $helperName })
            Assert-That ($helper.Count -eq 1 -and $helper[0].sha256 -ceq (Get-ReleaseHash (Join-Path $app $helperName))) 'Helper Windows có SHA-256 trong manifest đã ký'
        }
        $unexpected = Join-Path $app 'UnknownTool.exe'
        [IO.File]::WriteAllText($unexpected, 'không thuộc bộ runtime')
        Assert-Rejected {
            New-ReleasePackage -Version '1.2.3' -Rid $rid -AppDirectory $app -LauncherDirectory $launcher `
                -OutputDirectory (Join-Path $temp "rejected-exe-$rid") -Notes '' -SigningKey $key
        } "Từ chối EXE lạ trong publish $rid"
        Remove-Item -LiteralPath $unexpected
        if ($rid -eq 'linux-x64') {
            $unexpectedHelper = Join-Path $app $helperName
            [IO.File]::WriteAllText($unexpectedHelper, 'helper Windows lẫn nền tảng')
            Assert-Rejected {
                New-ReleasePackage -Version '1.2.3' -Rid $rid -AppDirectory $app -LauncherDirectory $launcher `
                    -OutputDirectory (Join-Path $temp 'rejected-linux-helper') -Notes '' -SigningKey $key
            } 'Từ chối helper Windows lọt vào publish Linux'
            Remove-Item -LiteralPath $unexpectedHelper
        }
    }
    Assert-Rejected { Assert-ReleaseVersion '1.2.3/../4' } 'Từ chối phiên bản chèn đường dẫn'
    Assert-Rejected { Assert-ReleaseVersion '01.2.3' } 'Từ chối phiên bản có số 0 thừa'
    Assert-Rejected { Assert-ReleaseVersion '65535.0.0' } 'Từ chối phiên bản không dùng được cho assembly Windows'
    Assert-Rejected { Test-ReleaseArtifacts -Directory (Join-Path $releaseDirectory 'win-x64') -Version '1.2.4' -Rid 'win-x64' -PublicKey $key } 'Từ chối gói khác phiên bản'

    $global:UpdateReleaseTestCommit = 'a' * 40
    $publicKeyPath = Join-Path $temp 'public-key.spki'
    [IO.File]::WriteAllText($publicKeyPath, [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
    $bootstrapHashes = [ordered]@{}
    foreach ($rid in @('win-x64', 'linux-x64')) {
        $bootstrapHashes[$rid] = Get-ReleaseHash (Join-Path $releaseDirectory "$rid/bootstrap.zip")
    }
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'public-key.spki'), [IO.File]::ReadAllText($publicKeyPath))
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'release.json'), ([ordered]@{
        version = '1.2.3'; tag = 'v1.2.3'; commit = $global:UpdateReleaseTestCommit
        builtUtc = '2026-01-01T00:00:00.0000000Z'; bootstrapSha256 = $bootstrapHashes
    } | ConvertTo-Json -Depth 4 -Compress))

    # Fake gh and git functions exercise the real publishing script without network or remote mutation.
    $global:UpdateReleaseTestReleases = @()
    $global:UpdateReleaseTestNextId = 1
    $global:UpdateReleaseTestMutations = 0
    function global:git {
        $arguments = @($args | ForEach-Object { [string]$_ })
        $global:LASTEXITCODE = 0
        if ($arguments.Count -lt 3 -or $arguments[0] -cne '-C') { throw 'Fake git chỉ hỗ trợ lệnh có -C.' }
        switch ($arguments[2]) {
            'rev-parse' {
                if ($arguments -contains 'HEAD' -or $arguments -contains '--verify') {
                    Write-Output $global:UpdateReleaseTestCommit
                    return
                }
                throw 'Fake git không hỗ trợ rev-parse này.'
            }
            'status' { return }
            'ls-remote' {
                Write-Output ("{0}`trefs/tags/v1.2.3" -f $global:UpdateReleaseTestCommit)
                return
            }
            default { throw "Fake git không hỗ trợ $($arguments[2])." }
        }
    }
    function global:gh {
        $arguments = @($args | ForEach-Object { [string]$_ })
        $global:LASTEXITCODE = 0

        if ($arguments.Count -ge 2 -and $arguments[0] -ceq 'repo' -and $arguments[1] -ceq 'view') {
            Write-Output '{"nameWithOwner":"ErfanMNM/VisionF","isPrivate":false}'
            return
        }
        if ($arguments.Count -ge 2 -and $arguments[0] -ceq 'api') {
            $apiArgs = @($arguments[1..($arguments.Count - 1)])
            if ($apiArgs[0] -ceq '-X') {
                if ($apiArgs.Count -lt 3 -or $apiArgs[1] -cne 'PATCH') { throw 'Fake gh chỉ hỗ trợ PATCH.' }
                $endpoint = $apiArgs[2]
                if ($endpoint -notmatch '/releases/(\d+)$') { throw "Fake gh không nhận diện release endpoint: $endpoint" }
                $release = @($global:UpdateReleaseTestReleases | Where-Object { $_.id -eq [int]$Matches[1] }) | Select-Object -First 1
                if (-not $release) { $global:LASTEXITCODE = 1; Write-Output 'Not Found (HTTP 404)'; return }
                for ($i = 3; $i -lt $apiArgs.Count; $i++) {
                    if ($apiArgs[$i] -ceq '-F' -and $i + 1 -lt $apiArgs.Count) {
                        $field = $apiArgs[++$i]
                        if ($field -match '^draft=(true|false)$') { $release.draft = $Matches[1] -ceq 'true' }
                        elseif ($field -match '^prerelease=(true|false)$') { $release.prerelease = $Matches[1] -ceq 'true' }
                    }
                }
                $global:UpdateReleaseTestMutations++
                Write-Output ($release | ConvertTo-Json -Depth 8 -Compress)
                return
            }

            $endpoint = [string]$apiArgs[0]
            if ($endpoint -match '/releases\?per_page=100$') {
                Write-Output (ConvertTo-Json -InputObject @($global:UpdateReleaseTestReleases) -Depth 8 -Compress)
                return
            }
            if ($endpoint -match '/releases/tags/(v[0-9.]+)$') {
                $tag = $Matches[1]
                $release = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -ceq $tag -and -not $_.draft }) | Select-Object -First 1
                if (-not $release) { $global:LASTEXITCODE = 1; Write-Output 'Not Found (HTTP 404)'; return }
                Write-Output ($release | ConvertTo-Json -Depth 8 -Compress)
                return
            }
            throw "Fake gh không hỗ trợ API $endpoint."
        }
        if ($arguments.Count -ge 3 -and $arguments[0] -ceq 'release' -and $arguments[1] -ceq 'create') {
            $tag = $arguments[2]
            $existingRelease = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -ceq $tag }).Count -gt 0
            if ($tag -cne 'v1.2.3' -or $existingRelease) {
                throw "Fake gh không thể tạo release $tag."
            }
            $releaseId = $global:UpdateReleaseTestNextId
            $global:UpdateReleaseTestNextId++
            $global:UpdateReleaseTestReleases += [ordered]@{
                id = $releaseId; tag_name = $tag; draft = $true
                prerelease = $false; assets = @()
            }
            $global:UpdateReleaseTestMutations++
            Write-Output "https://github.com/ErfanMNM/VisionF/releases/$tag"
            return
        }
        if ($arguments.Count -ge 5 -and $arguments[0] -ceq 'release' -and $arguments[1] -ceq 'upload') {
            $tag = $arguments[2]
            $release = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -ceq $tag }) | Select-Object -First 1
            if (-not $release -or -not $release.draft) { throw 'Fake gh chỉ cho upload vào draft release.' }
            foreach ($assetPath in $arguments[5..($arguments.Count - 1)]) {
                $item = Get-Item -LiteralPath $assetPath
                $release.assets += [ordered]@{
                    name = $item.Name; size = $item.Length
                    digest = 'sha256:' + (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
            $global:UpdateReleaseTestMutations++
            Write-Output 'uploaded'
            return
        }
        throw "Fake gh không hỗ trợ lệnh: $($arguments -join ' ')"
    }

    function Add-TestRelease {
        param([string]$Version, [bool]$Prerelease)
        $releaseId = $global:UpdateReleaseTestNextId
        $global:UpdateReleaseTestNextId++
        $global:UpdateReleaseTestReleases += [ordered]@{
            id = $releaseId; tag_name = "v$Version"; draft = $false
            prerelease = $Prerelease; assets = @()
        }
    }

    $githubReleaseScript = Join-Path $PSScriptRoot 'github-release.ps1'
    $releaseParameters = @{ Version = '1.2.3'; ReleaseDirectory = $releaseDirectory; PublicKeyPath = $publicKeyPath }
    Add-TestRelease -Version '1.2.4' -Prerelease $false
    $mutationsBefore = $global:UpdateReleaseTestMutations
    Assert-Rejected { & $githubReleaseScript -Action Upload @releaseParameters } 'Chặn pilot không mới hơn stable'
    Assert-That ($global:UpdateReleaseTestMutations -eq $mutationsBefore) 'Từ chối pilot cũ không đổi GitHub state'

    $global:UpdateReleaseTestReleases = @()
    & $githubReleaseScript -Action Upload @releaseParameters
    $pilot = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -ceq 'v1.2.3' }) | Select-Object -First 1
    Assert-That ($pilot -and -not $pilot.draft -and $pilot.prerelease) 'Upload tạo draft, xác minh asset rồi công bố pilot'
    Assert-That ($pilot.assets.Count -eq 8) 'Pilot mang đủ sáu asset nền tảng, metadata và khóa công khai'
    Assert-That (@($pilot.assets.name | Where-Object { $_ -in @('manifest-win-x64.json', 'package-win-x64.zip', 'bootstrap-win-x64.zip', 'manifest-linux-x64.json', 'package-linux-x64.zip', 'bootstrap-linux-x64.zip', 'release.json', 'public-key.spki') }).Count -eq 8) 'Tên asset GitHub giữ đúng contract per-RID'

    $mutationsBefore = $global:UpdateReleaseTestMutations
    $packageAsset = @($pilot.assets | Where-Object { $_.name -ceq 'package-win-x64.zip' })[0]
    $originalDigest = $packageAsset.digest
    $packageAsset.digest = 'sha256:' + ('0' * 64)
    Assert-Rejected { & $githubReleaseScript -Action Promote @releaseParameters } 'Từ chối promote khi SHA asset từ xa bị sửa'
    Assert-That ($global:UpdateReleaseTestMutations -eq $mutationsBefore) 'Asset bị sửa không đổi trạng thái release'
    $packageAsset.digest = $originalDigest

    $missingAsset = @($pilot.assets | Where-Object { $_.name -ceq 'manifest-linux-x64.json' })[0]
    $pilot.assets = @($pilot.assets | Where-Object { $_.name -cne 'manifest-linux-x64.json' })
    Assert-Rejected { & $githubReleaseScript -Action Promote @releaseParameters } 'Từ chối promote khi thiếu asset platform'
    Assert-That ($global:UpdateReleaseTestMutations -eq $mutationsBefore) 'Thiếu asset không đổi trạng thái release'
    $pilot.assets += $missingAsset

    Add-TestRelease -Version '1.2.4' -Prerelease $true
    Assert-Rejected { & $githubReleaseScript -Action Promote @releaseParameters } 'Chỉ promote pilot mới nhất'
    $global:UpdateReleaseTestReleases = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -cne 'v1.2.4' })
    Add-TestRelease -Version '1.2.4' -Prerelease $false
    $mutationsBefore = $global:UpdateReleaseTestMutations
    Assert-Rejected { & $githubReleaseScript -Action Promote @releaseParameters } 'Chặn promote không mới hơn stable'
    Assert-That ($global:UpdateReleaseTestMutations -eq $mutationsBefore) 'Promote phiên bản cũ không đổi GitHub state'
    $global:UpdateReleaseTestReleases = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -cne 'v1.2.4' })
    Add-TestRelease -Version '1.2.2' -Prerelease $false
    & $githubReleaseScript -Action Promote @releaseParameters
    $pilot = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -ceq 'v1.2.3' }) | Select-Object -First 1
    Assert-That (-not $pilot.draft -and -not $pilot.prerelease) 'Promote pilot mới nhất sang stable'

    $assetCountBeforeWithdraw = $pilot.assets.Count
    & $githubReleaseScript -Action Withdraw -Version '1.2.3'
    $pilot = @($global:UpdateReleaseTestReleases | Where-Object { $_.tag_name -ceq 'v1.2.3' }) | Select-Object -First 1
    Assert-That ($pilot.draft -and $pilot.assets.Count -eq $assetCountBeforeWithdraw) 'Withdraw ẩn release và giữ asset để phục hồi'

    $mutationsBefore = $global:UpdateReleaseTestMutations
    $packagePath = Join-Path $releaseDirectory 'win-x64/package.zip'
    $originalPackage = [IO.File]::ReadAllBytes($packagePath)
    [IO.File]::AppendAllText($packagePath, 'tampered')
    Assert-Rejected { & $githubReleaseScript -Action Upload @releaseParameters } 'Từ chối upload nếu package cục bộ bị sửa'
    Assert-That ($global:UpdateReleaseTestMutations -eq $mutationsBefore) 'Package bị sửa không tạo mutation GitHub'
    [IO.File]::WriteAllBytes($packagePath, $originalPackage)
    $manifestPath = Join-Path $releaseDirectory 'linux-x64/manifest.json'
    $heldManifest = Join-Path $temp 'held-manifest.json'
    Move-Item -LiteralPath $manifestPath -Destination $heldManifest
    Assert-Rejected { & $githubReleaseScript -Action Upload @releaseParameters } 'Từ chối upload khi thiếu manifest cục bộ'
    Assert-That ($global:UpdateReleaseTestMutations -eq $mutationsBefore) 'Manifest thiếu không tạo mutation GitHub'
    Move-Item -LiteralPath $heldManifest -Destination $manifestPath

    [IO.File]::AppendAllText((Join-Path $releaseDirectory 'win-x64/package.zip'), 'tampered')
    Assert-Rejected { Test-ReleaseArtifacts -Directory (Join-Path $releaseDirectory 'win-x64') -Version '1.2.3' -Rid 'win-x64' -PublicKey $key } 'Từ chối ZIP bị sửa sau khi ký'
    Write-Host "Đã đạt $passed kiểm tra release tooling."
}
finally {
    if (Test-Path Function:global:gh) { Remove-Item Function:global:gh }
    if (Test-Path Function:global:git) { Remove-Item Function:global:git }
    if ($global:UpdateReleaseTestSavedGh) { Set-Item Function:global:gh -Value $global:UpdateReleaseTestSavedGh.ScriptBlock }
    if ($global:UpdateReleaseTestSavedGit) { Set-Item Function:global:git -Value $global:UpdateReleaseTestSavedGit.ScriptBlock }
    if ($global:UpdateReleaseTestSavedLastExitCode) { $global:LASTEXITCODE = $global:UpdateReleaseTestSavedLastExitCode.Value }
    else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    Remove-Variable -Name UpdateReleaseTestCommit, UpdateReleaseTestReleases, UpdateReleaseTestNextId,
        UpdateReleaseTestMutations, UpdateReleaseTestSavedGh, UpdateReleaseTestSavedGit,
        UpdateReleaseTestSavedLastExitCode -Scope Global -ErrorAction SilentlyContinue
    $key.Dispose()
    Remove-ReleaseTempDirectory -Directory $temp -Parent $tempParent -Prefix 'tantien-release-test-'
}
