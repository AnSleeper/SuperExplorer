# 超级资源管理器 · 一键发布脚本
# 用法：改完代码、bump 好 csproj 里的 <Version> 后，运行：
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1
# 可选参数：
#   -Notes "自定义发布说明"   不给则自动用 GitHub Compare API 生成
# 流程：dotnet publish -> 打 zip（历史版本/ 只留最近 3 个） -> 创建或更新 GitHub Release -> 上传附件
# 凭据取自 Windows 凭据管理器（git credential），无需手动配置。

param([string]$Notes)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 关进度条，否则 PS5.1 传大文件极慢
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'src\SuperExplorer'

# ---------- 1. 版本号 ----------

$csproj = Join-Path $src 'SuperExplorer.csproj'
$ver = (Select-Xml -Path $csproj -XPath '//Version' | Select-Object -First 1).Node.InnerText.Trim()
if (-not $ver) { throw '无法从 SuperExplorer.csproj 读取 <Version>' }
$tag = "v$ver"
$zipName = "SuperExplorer-$ver-win-x64.zip"
Write-Host "== 版本 $ver  (tag $tag) =="

# ---------- 2. 编译发布（缓存重定向进项目目录，不占 C 盘） ----------

$env:NUGET_PACKAGES  = Join-Path $root '.nuget'
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:XDG_DATA_HOME   = Join-Path $root '.dotnet'

Write-Host "== dotnet publish =="
# 先清空发布目录再完整重发：增量发布偶发丢失运行库 DLL，宁可多花一次编译时间
$pub = Join-Path $src 'bin\Release\net8.0-windows\win-x64\publish'
if (Test-Path $pub) { [System.IO.Directory]::Delete($pub, $true) }
Push-Location $src
try { dotnet publish -c Release | Out-Null }
finally { Pop-Location }

if (-not (Test-Path (Join-Path $pub 'SuperExplorer.exe'))) { throw "发布产物不存在：$pub" }

# ---------- 3. 打 zip（并校验完整性） ----------

$outDir = Join-Path $root '历史版本'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zip = Join-Path $outDir $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force

# 上传前强制校验：zip 必须含 exe + 5 个运行库 DLL，防止把不完整的包发出去
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required = @('SuperExplorer.exe', 'D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll',
              'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll')
$zr = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $names = @($zr.Entries | ForEach-Object { $_.Name }) }
finally { $zr.Dispose() }
$missing = @($required | Where-Object { $names -notcontains $_ })
if ($missing.Count -gt 0) { throw ("zip 缺少文件，已中止上传：{0}" -f ($missing -join ', ')) }

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host ("== zip 完成（{0} 个文件校验通过）：{1}  ({2:N1} MB)  SHA256 {3}…" -f $names.Count, $zipName, ((Get-Item $zip).Length/1MB), $sha.Substring(0,16))

# ---------- 4. GitHub 凭据与仓库信息 ----------

$env:GCM_INTERACTIVE = 'never'
$fill = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = $null; $user = $null
foreach ($line in $fill) {
    if ($line -match '^password=(.+)$') { $token = $Matches[1] }
    if ($line -match '^username=(.+)$') { $user = $Matches[1] }
}
if (-not $token) { throw 'Windows 凭据管理器里没有 github.com 凭据，先 git push 一次让它记住' }

$remote = git remote get-url origin
if ($remote -notmatch 'github\.com[/:]([^/]+/[^/.]+?)(\.git)?$') { throw "无法解析 origin 远程：$remote" }
$slug = $Matches[1]
$api = "https://api.github.com/repos/$slug"
Write-Host "== 仓库 $slug（账号 $user）=="

# ---------- 5. HTTP 帮助函数（直连优先，失败回退本地代理，带重试） ----------

function Invoke-Gh {
    param([string]$Method, [string]$Uri, $BodyObj, [string]$InFile, [string]$ContentType)
    $headers = @{ Authorization = "Bearer $script:token"; Accept = 'application/vnd.github+json' }
    $body = $null
    if ($BodyObj) {
        $json = $BodyObj | ConvertTo-Json -Depth 5
        $body = [Text.Encoding]::UTF8.GetBytes($json)
        $ContentType = 'application/json; charset=utf-8'
    }
    $proxies = @($null, 'http://127.0.0.1:7897')
    $err = $null
    foreach ($attempt in 1..4) {
        foreach ($p in $proxies) {
            try {
                $a = @{ Method = $Method; Uri = $Uri; Headers = $headers; TimeoutSec = 600 }
                if ($body)   { $a.Body = $body; $a.ContentType = $ContentType }
                if ($InFile) { $a.InFile = $InFile; $a.ContentType = $ContentType }
                if ($p)      { $a.Proxy = $p }
                return Invoke-RestMethod @a
            } catch {
                $err = $_
                # 404 是「资源不存在」的正常业务结果（如 Release 尚未创建），不重试
                $resp = $null
                try { $resp = $_.Exception.Response } catch { }
                if ($resp -and [int]$resp.StatusCode -eq 404) { return $null }
            }
        }
        Write-Host "  网络失败（第 $attempt 轮）：$($err.Exception.Message)  15 秒后重试..."
        Start-Sleep -Seconds 15
    }
    throw "GitHub 请求最终失败：$err"
}

# ---------- 6. 发布说明（Compare API 自动生成，避免本地 tag / 中文乱码问题） ----------

if (-not $Notes) {
    $logTxt = ''
    try {
        $prevTag = (Invoke-Gh GET "$api/releases/latest").tag_name
        if ($prevTag -and $prevTag -ne $tag) {
            $cmp = Invoke-Gh GET "$api/compare/$prevTag...HEAD"
            $msgs = @($cmp.commits) | ForEach-Object { ($_.commit.message -split "`r?`n")[0] } | Where-Object { $_ }
            if ($msgs) { $logTxt = ($msgs | ForEach-Object { "- $_" }) -join "`n" }
        }
    } catch { }
    if (-not $logTxt) { $logTxt = "- v$ver 版本更新" }
    $Notes = "## 下载即用（Windows x64）`n`n下载下方 **$zipName**，解压后双击 ``SuperExplorer.exe`` 即可运行。免安装、免依赖（自带 .NET 运行时）；设置与日志保存在 exe 旁。`n`nSHA256：``$sha```n`n## 更新内容`n`n$logTxt`n`n源码与历史版本见仓库主页；遇到问题欢迎提 Issue。"
}
else { $Notes = $Notes + "`n`nSHA256：``$sha``" }

# ---------- 7. 创建或更新 Release ----------

$release = $null
try { $release = Invoke-Gh GET "$api/releases/tags/$tag" } catch { }

if ($release) {
    Write-Host "== Release $tag 已存在，更新说明并替换附件 =="
    Invoke-Gh PATCH "$api/releases/$($release.id)" @{ name = $tag; body = $Notes } | Out-Null
    foreach ($asset in @($release.assets)) {
        # 清掉所有旧命名的 zip 变体（SuperExplorer-v0.1.1-… / SuperExplorer-0.1.1-… 等）
        if ($asset.name -match '^SuperExplorer.*win-x64\.zip$') {
            Invoke-Gh DELETE "$api/releases/assets/$($asset.id)" | Out-Null
            Write-Host "  已删除旧附件 $($asset.name)"
        }
    }
} else {
    Write-Host "== 创建 Release $tag =="
    $release = Invoke-Gh POST "$api/releases" @{
        tag_name = $tag; target_commitish = 'main'; name = $tag
        body = $Notes; draft = $false; prerelease = $false
    }
}

# ---------- 8. 上传 zip 附件 ----------

$upUri = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$zipName"
Invoke-Gh POST $upUri -InFile $zip -ContentType 'application/zip' | Out-Null

# ---------- 9. 本地只保留最近 3 个包 ----------

# 每个包约 60 MB，不清理会无限膨胀；GitHub Release 上仍保留全部历史版本。
$keep = 3
$localZips = @(Get-ChildItem -Path $outDir -Filter 'SuperExplorer-*-win-x64.zip' -File |
               Sort-Object LastWriteTime -Descending)
if ($localZips.Count -gt $keep) {
    foreach ($old in ($localZips | Select-Object -Skip $keep)) {
        Remove-Item $old.FullName -Force
        Write-Host "  已清理本地旧包 $($old.Name)"
    }
}

Write-Host ''
Write-Host "== 完成 ✅ =="
Write-Host "下载页：https://github.com/$slug/releases"
Write-Host "本页：  https://github.com/$slug/releases/tag/$tag"
Write-Host "本地包：$zip"
