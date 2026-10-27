# 从小米官网下载界面字体 MiSans，解出要用的 4 个字重到 src\JableLens.App\Assets\Fonts
# MiSans 可免费商用，嵌入软件需注明（设置页底部已注明）；字体文件不放进仓库
$ErrorActionPreference = 'Stop'
$dst = Join-Path $PSScriptRoot '..\src\JableLens.App\Assets\Fonts'
$zip = Join-Path $env:TEMP 'MiSans.zip'
New-Item -ItemType Directory -Force $dst | Out-Null
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest 'https://hyperos.mi.com/font-download/MiSans.zip' -OutFile $zip -UseBasicParsing
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    # 只要这 4 个：界面用到的常规、半粗、粗、最粗（见 Controls\Weights.cs）
    foreach ($w in 'Regular', 'Semibold', 'Bold', 'Heavy') {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($z.GetEntry("MiSans/ttf/MiSans-$w.ttf"), (Join-Path $dst "MiSans-$w.ttf"), $true)
    }
}
finally { $z.Dispose(); Remove-Item $zip }
Write-Host "完成：$dst"
