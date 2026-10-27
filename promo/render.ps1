# 用 Edge 无头模式把 promo.html 的每一屏渲染成 PNG
param([int]$Width = 1920, [int]$Height = 1080, [string[]]$Scenes = @('w1', 'w2', 'w3', 'w4'), [string]$Prefix = 'Jable-Windows')
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$html = (Join-Path $PSScriptRoot 'promo.html') -replace '\\', '/'
$profile = Join-Path $env:TEMP 'jable-promo-edge'
$i = 0
foreach ($s in $Scenes) {
    $i++
    $out = Join-Path $PSScriptRoot "$Prefix-$i.png"
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 "--user-data-dir=$profile" "--window-size=$Width,$Height" "--screenshot=$out" "file:///$html#$s" 2>$null | Out-Null
    Write-Host $out
}
