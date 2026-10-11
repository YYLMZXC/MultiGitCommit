<#
.SYNOPSIS
    下载便携版 MinGit 并解压到 src\git\，使 MultiGitCommit 无需用户额外安装 git。

.DESCRIPTION
    MinGit 是 Git for Windows 官方发布的免安装精简版（约 45MB），
    解压到 src\git\ 后，csproj 会自动把它复制到输出目录的 git\ 子目录，
    程序启动时由 GitLocator 自动检测并优先使用（软件目录便携版 > 常见安装位置 > 系统 PATH）。

    脚本依次尝试以下镜像（前两个为国内镜像，无需代理）：
    1. 华为云镜像  mirrors.huaweicloud.com
    2. npmmirror   registry.npmmirror.com
    3. GitHub 官方 releases（可能需要代理）

.PARAMETER Version
    MinGit 版本号，默认 2.50.1。完整列表见：
    https://github.com/git-for-windows/git/releases

.EXAMPLE
    .\download-portable-git.ps1
    .\download-portable-git.ps1 -Version 2.50.1
#>
param(
    [string]$Version = "2.50.1",
    [string]$Destination = (Join-Path $PSScriptRoot "src\git")
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$fileName = "MinGit-$Version-64-bit.zip"
$tag = "v$Version.windows.1"
$urls = @(
    "https://mirrors.huaweicloud.com/git-for-windows/$tag/$fileName",
    "https://registry.npmmirror.com/-/binary/git-for-windows/$tag/$fileName",
    "https://github.com/git-for-windows/git/releases/download/$tag/$fileName"
)

$tempZip = Join-Path $env:TEMP $fileName
$tempDir = Join-Path $env:TEMP "mingit-extract-$Version"

Write-Host "==> MinGit $Version (64-bit)"
Write-Host "==> 目标目录：$Destination"

# 1) 下载（依次尝试镜像）
$downloaded = $false
foreach ($url in $urls) {
    Write-Host "==> 尝试下载：$url"
    try {
        Invoke-WebRequest -Uri $url -OutFile $tempZip
        $downloaded = $true
        Write-Host "==> 下载成功。"
        break
    }
    catch {
        Write-Warning "下载失败（$($_.Exception.Message)），尝试下一个镜像..."
    }
}
if (-not $downloaded) {
    throw "所有镜像均下载失败，请检查网络或手动下载 $fileName 解压到 $Destination"
}

# 2) 解压到临时目录并校验结构
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempDir
$gitExe = Join-Path $tempDir "cmd\git.exe"
if (-not (Test-Path $gitExe)) {
    throw "压缩包结构异常：未找到 cmd\git.exe，请确认下载的是 MinGit 完整包"
}

# 3) 替换目标目录
if (Test-Path $Destination) {
    Write-Host "==> 已存在旧版本，正在替换..."
    Remove-Item $Destination -Recurse -Force
}
New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
Move-Item -Path $tempDir -Destination $Destination

Remove-Item $tempZip -Force -ErrorAction SilentlyContinue

# 4) 校验可执行（使用移动后的目标路径）
$destGitExe = Join-Path $Destination "cmd\git.exe"
$versionOutput = & $destGitExe --version
Write-Host "==> 完成：$destGitExe"
Write-Host "==> $versionOutput"
Write-Host "==> 重新构建（dotnet build / publish）后，git\ 将自动复制到输出目录。"
