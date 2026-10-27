# 生成安装包 release\Jable-Setup-<版本>.exe
# 1. 自包含发布（带 .NET 运行时）到 publish\Jable
# 2. 用 Inno Setup 打成安装包
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw '没找到 Inno Setup 6（winget install JRSoftware.InnoSetup）' }

# WebView2 运行时的在线安装引导程序（微软官方，约 2 MB；目标电脑没有 WebView2 时才会运行）
$wv2 = Join-Path $PSScriptRoot 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $wv2)) { Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wv2 -UseBasicParsing }

$out = Join-Path $root 'publish\Jable'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\JableLens.App') -c Release -r win-x64 --self-contained true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

& $iscc /Q (Join-Path $PSScriptRoot 'Jable.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC 失败' }
Get-ChildItem (Join-Path $root 'release') -Filter 'Jable-Setup-*.exe' | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
