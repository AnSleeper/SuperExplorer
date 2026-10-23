# 超级资源管理器 · 一键发布脚本
# 用法：改完代码、bump 好 csproj 里的 <Version> 后，运行：
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1
# 可选参数：
#   -Notes "自定义发布说明"   不给则自动用 git log 生成
# 流程：dotnet publish -> 打 zip -> 创建或更新 GitHub Release -> 上传附件
# 凭据取自 Windows 凭据管理器（git credential），无需手动配置。

param([string]$Notes)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 关进度条，否则 PS5.1 传大文件极慢
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]Tls12

$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'src\SuperExplorer'
$pkgName = 'SuperExplorer-win-x64'   # zip 内程序名（含 -版本- 后缀）

# ---------- 1. 版本号 ----------

$csproj = Join-Path $src 'SuperExplorer.csproj'
$ver = (Select-Xml -Path $csproj -XPath '//Version' | Select-Object -First 1).Node.InnerText.Trim()
if (-not $ver) { throw '无法从 SuperExplorer.csproj 读取 <Version>' }
$tag  = "v$ver"
$zipName = "SuperExplorer-$ver-win-x64.zip"
Write-Host "== 版本 $ver  (tag $tag) =="

# ---------- 2. 编译发布（缓存重定向进项目目录，不占 C 盘） ----------

$env:NUGET_PACKAGES   = Join-Path $root '.nuget'
$env:DOTNET_CLI_HOME  = Join-Path $root '.dotnet'
$env:XDG_DATA_HOME    = Join-Path $root '.dotnet'

Write-Host "== dotnet publish =="
Push-Location $src
try { dotnet publish -c Release | Out-Null }
finally { Pop-Location }

$pub = Join-Path $src 'bin\Release\net8.0-windows\win-x64\publish'
if (-not (Test-Path (Join-Path $pub 'SuperExplorer.exe'))) { throw "发布产物不存在：$pub" }

# ---------- 3. 打 zip ----------

$outDir = Join-Path $root '_publish_tmp'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zip = Join-Path $outDir $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host ("== zip 完成：{0}  ({1:N1} MB)  SHA256 {2}" -f $zipName, ((Get-Item $zip).Length/1MB), $sha.Substring(0,16))

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
Write-Host "== 仓库 $slug（账号 $user）=="

# ---------- 5. 发布说明 ----------

if (-not $Notes) {
    # git 输出是 UTF-8，先切控制台编码避免中文乱码
    $oldEnc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $lastTag = git describe --abbrev=0 --tags 2>$null
    [Console]::OutputEncoding = $oldEnc
    $logTxt = ''
    if ($lastTag) {
        $commits = git log --oneline "$lastTag..HEAD" 2>$null
        if ($commits) { $logTxt = ($commits | ForEach-Object { '- ' + ($_ -replace '^[0-9a-f]{7,} ', '') }) -join "`n" }
    }
    if (-not $logTxt) { $logTxt = "- v$ver 版本更新" }
    $Notes = "## 下载即用（Windows x64）`n`n下载下方 **$zipName**，解压后双击 ``SuperExplorer.exe`` 即可运行。免安装、免依赖（自带 .NET 运行时）；设置与日志保存在 exe 旁。`n`nSHA256：``$sha```n`n## 更新内容`n`n$logTxt`n`n源码与历史版本见仓库主页；遇到问题欢迎提 Issue。"
}
else { $Notes = $Notes + "`n`nSHA256：``$sha``" }

# ---------- 6. HTTP 帮助函数（直连优先，失败回退本地代理，带重试） ----------

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
                $args = @{ Method = $Method; Uri = $Uri; Headers = $headers; TimeoutSec = 600 }
                if ($body)    { $args.Body = $body; $args.ContentType = $ContentType }
                if ($InFile)  { $args.InFile = $InFile; $args.ContentType = $ContentType }
                if ($p)       { $args.Proxy = $p }
                return Invoke-RestMethod @args
            } catch { $err = $_ }
        }
        Write-Host "  网络失败（第 $attempt 轮）：$($err.Exception.Message)  15 秒后重试..."
        Start-Sleep -Seconds 15
    }
    throw "GitHub 请求最终失败：$err"
}

# ---------- 7. 创建或更新 Release ----------

$api = "https://api.github.com/repos/$slug"
$release = $null
try { $release = Invoke-Gh GET "$api/releases/tags/$tag" } catch { }

if ($release) {
    Write-Host "== Release $tag 已存在，更新说明并替换附件 =="
    Invoke-Gh PATCH "$api/releases/$($release.id)" @{ name = $tag; body = $Notes } | Out-Null
    foreach ($asset in @($release.assets)) {
        if ($asset.name -eq $zipName) {
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

Write-Host ''
Write-Host "== 完成 ✅ =="
Write-Host "下载页：https://github.com/$slug/releases"
Write-Host "本页：  https://github.com/$slug/releases/tag/$tag"
Write-Host "本地包：$zip"
