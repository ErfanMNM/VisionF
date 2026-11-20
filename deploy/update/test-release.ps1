#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'UpdateRelease.psm1') -Force -DisableNameChecking
$tempParent = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
$temp = Join-Path $tempParent ('tantien-release-test-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temp)
$key = [Security.Cryptography.RSA]::Create(3072)
$passed = 0

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
            Assert-That ($archive.Entries.Count -eq 2) "Gói $rid chỉ có runtime, không chứa cấu hình, license, symbols hoặc launcher"
            if ($rid -eq 'linux-x64') {
                $entry = $archive.GetEntry($mainName)
                Assert-That ((($entry.ExternalAttributes -shr 16) -band 0x1FF) -eq 0x1ED) 'Giữ quyền thực thi 0755 trong ZIP Linux'
            }
        }
        finally { $archive.Dispose() }
        $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'bootstrap.zip'))
        try { Assert-That ($null -ne $archive.GetEntry($launcherName)) "Bootstrap $rid có trình khởi chạy" }
        finally { $archive.Dispose() }
    }
    Assert-Rejected { Assert-ReleaseVersion '1.2.3/../4' } 'Từ chối phiên bản chèn đường dẫn'
    Assert-Rejected { Assert-ReleaseVersion '01.2.3' } 'Từ chối phiên bản có số 0 thừa'
    Assert-Rejected { Assert-ReleaseVersion '65535.0.0' } 'Từ chối phiên bản không dùng được cho assembly Windows'
    Assert-Rejected { Test-ReleaseArtifacts -Directory (Join-Path $releaseDirectory 'win-x64') -Version '1.2.4' -Rid 'win-x64' -PublicKey $key } 'Từ chối gói khác phiên bản'

    # Fake S3 cục bộ chỉ dùng trong bài kiểm tra; không có mạng hoặc credential thật.
    $global:UpdateReleaseTestRemote = @{}
    $global:UpdateReleaseTestFailKey = ''
    $global:UpdateReleaseTestSavedAws = Get-Command aws -CommandType Function -ErrorAction SilentlyContinue
    function global:aws {
        $arguments = @($args)
        $operation = $arguments[1]
        $objectKey = [string]$arguments[[Array]::IndexOf($arguments, '--key') + 1]
        if ($operation -eq 'get-object') {
            if (-not $global:UpdateReleaseTestRemote.ContainsKey($objectKey)) {
                $global:LASTEXITCODE = 1
                Write-Output 'An error occurred (NoSuchKey) when calling the GetObject operation.'
                return
            }
            $destination = [string]$arguments[[Array]::IndexOf($arguments, '--key') + 2]
            [IO.File]::WriteAllBytes($destination, $global:UpdateReleaseTestRemote[$objectKey])
            $global:LASTEXITCODE = 0
            Write-Output (@{ ETag = '"' + (Get-FileHash -LiteralPath $destination -Algorithm MD5).Hash + '"' } | ConvertTo-Json -Compress)
            return
        }
        if ($operation -ne 'put-object') { throw 'Fake S3 chỉ hỗ trợ get-object và put-object.' }
        if ($objectKey -ceq $global:UpdateReleaseTestFailKey) {
            $global:LASTEXITCODE = 1
            Write-Output 'Simulated R2 write failure'
            return
        }
        $source = [string]$arguments[[Array]::IndexOf($arguments, '--body') + 1]
        $global:UpdateReleaseTestRemote[$objectKey] = [IO.File]::ReadAllBytes($source)
        $global:LASTEXITCODE = 0
        Write-Output '{}'
    }
    $publicKeyPath = Join-Path $temp 'public-key.spki'
    [IO.File]::WriteAllText($publicKeyPath, [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
    $r2 = Join-Path $PSScriptRoot 'r2-release.ps1'
    $parameters = @{
        Version = '1.2.3'; AccountId = '00000000000000000000000000000000'; Bucket = 'test-updates'
        ReleaseDirectory = $releaseDirectory; PublicKeyPath = $publicKeyPath
    }
    $global:UpdateReleaseTestFailKey = 'releases/1.2.3/linux-x64/manifest.json'
    Assert-Rejected { & $r2 -Action Upload @parameters } 'Upload dừng khi một trong hai nền tảng thất bại'
    Assert-That (-not $global:UpdateReleaseTestRemote.ContainsKey('channels/pilot.json')) 'Không công bố pilot nếu chưa đủ hai gói'
    $global:UpdateReleaseTestFailKey = ''
    & $r2 -Action Upload @parameters
    Assert-That ($global:UpdateReleaseTestRemote.ContainsKey('channels/pilot.json')) 'Upload tiếp tục an toàn và công bố pilot khi đủ cả hai gói'
    Assert-That (-not $global:UpdateReleaseTestRemote.ContainsKey('channels/stable.json')) 'Upload không tự công bố stable'
    Assert-Rejected { & $r2 -Action Promote @parameters } 'Promote yêu cầu xác nhận kiểm thử pilot'
    & $r2 -Action Promote @parameters -PilotVerified
    Assert-That ($global:UpdateReleaseTestRemote.ContainsKey('channels/stable.json')) 'Promote đúng các gói pilot sang stable'
    & $r2 -Action Withdraw @parameters -Channel stable
    Assert-That ([Text.Encoding]::UTF8.GetString($global:UpdateReleaseTestRemote['channels/stable.json']) -ceq '{}') 'Rút bản lỗi bằng con trỏ rỗng'
    Assert-Rejected { & $r2 -Action Withdraw @parameters -Channel stable } 'Không rút nhầm phiên bản khi con trỏ đã đổi'
    [IO.File]::AppendAllText((Join-Path $releaseDirectory 'win-x64/package.zip'), 'tampered')
    Assert-Rejected { Test-ReleaseArtifacts -Directory (Join-Path $releaseDirectory 'win-x64') -Version '1.2.3' -Rid 'win-x64' -PublicKey $key } 'Từ chối ZIP bị sửa sau khi ký'
    Write-Host "Đã đạt $passed kiểm tra release tooling."
}
finally {
    if (Test-Path Function:global:aws) { Remove-Item Function:global:aws }
    if (Get-Variable -Name UpdateReleaseTestSavedAws -Scope Global -ErrorAction SilentlyContinue) {
        if ($global:UpdateReleaseTestSavedAws) { Set-Item Function:global:aws -Value $global:UpdateReleaseTestSavedAws.ScriptBlock }
    }
    Remove-Variable -Name UpdateReleaseTestRemote, UpdateReleaseTestFailKey, UpdateReleaseTestSavedAws -Scope Global -ErrorAction SilentlyContinue
    $key.Dispose()
    Remove-ReleaseTempDirectory -Directory $temp -Parent $tempParent -Prefix 'tantien-release-test-'
}
