param(
    [string]$GamePath = 'D:\steam\steamapps\common\Stardew Valley',
    [ValidateSet('QuickTest', 'Formal')][string]$Profile = 'QuickTest'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sdkPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdkPath)) { $sdkPath = (Get-Command dotnet -ErrorAction Stop).Source }
if (!(Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.dll'))) { throw 'GamePath must contain StardewModdingAPI.dll.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $projectRoot
try {
    $manifest = Get-Content -LiteralPath 'src/AngelWings.VisualPreview/manifest.json' -Raw | ConvertFrom-Json
    if ($Profile -eq 'Formal') {
        $manifest.Version = $manifest.Version -replace '-quick-test$', ''
        $manifest.Description = 'Formal-rule build: optional Swim 1.9.0 surface flight switching; underwater takeoff blocked; native-scale wing displays; occlusion-r9 art; eight-heart, 369-flower quest.'
    }
    $releaseRoot = Join-Path $projectRoot 'release'
    $zipPath = Join-Path $releaseRoot ("AngelWingsVisualPreview-" + $manifest.Version + '.zip')
    if (Test-Path -LiteralPath $zipPath) { throw "Release already exists; use a new version instead of overwriting: $zipPath" }
    python 'tools/art/check_approved_animation.py'
    if ($LASTEXITCODE -ne 0) { throw 'Art attachment validation failed.' }
    python 'tools/art/check_butterfly_animation.py'
    if ($LASTEXITCODE -ne 0) { throw 'Butterfly anatomy/perspective regression failed.' }
    python 'tools/art/pack_preview_atlases.py'
    if ($LASTEXITCODE -ne 0) { throw 'Art atlas packing failed.' }
    python 'tools/art/check_wing_furniture.py'
    if ($LASTEXITCODE -ne 0) { throw 'Native-scale wing furniture validation failed.' }
    & $sdkPath build 'src/AngelWings.VisualPreview/AngelWings.VisualPreview.csproj' -c Release "-p:GamePath=$GamePath" "-p:AngelWingsProfile=$Profile" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
    & (Join-Path $projectRoot 'validate-flight.ps1') -GamePath $GamePath -Profile $Profile
    $sourcePath = if ($Profile -eq 'Formal') {
        Join-Path $projectRoot 'src/AngelWings.VisualPreview/bin/Release/formal/net6.0'
    } else { Join-Path $projectRoot 'src/AngelWings.VisualPreview/bin/Release/net6.0' }
    $packageRoot = if ($Profile -eq 'Formal') {
        Join-Path $projectRoot 'build/formal/AngelWingsVisualPreview'
    } else { Join-Path $projectRoot 'build/AngelWingsVisualPreview' }
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    foreach ($fileName in @('AngelWings.VisualPreview.dll', 'manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $sourcePath $fileName) -Destination $packageRoot -Force
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8
    foreach ($folderName in @('assets', 'i18n')) {
        $destination = Join-Path $packageRoot $folderName
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path (Join-Path $sourcePath $folderName) '*') -Destination $destination -Recurse -Force
    }
    $hearts = if ($Profile -eq 'Formal') { 8 } else { 3 }
    $flowers = if ($Profile -eq 'Formal') { 369 } else { 0 }
    $publicReadme = Get-Content -LiteralPath (Join-Path $projectRoot 'README.md') -Raw
    $playerGuide = ($publicReadme -split '(?m)^## 从源码开发\s*$', 2)[0].Trim()
    $packageReadme = @"
# Angel Wings $($manifest.Version)

Profile: $Profile. Quest: Haley $hearts hearts / $flowers flowers.
Buy permanent flight from Hat Mouse for 1,000g.

$playerGuide
"@
    Set-Content -LiteralPath (Join-Path $packageRoot 'README.md') -Value $packageReadme -Encoding UTF8
    $wingGuide = @"
# Wing appearances / 翅膀外观

This package uses $Profile rules: Haley $hearts hearts / $flowers flowers.
本包任务条件：海莉 $hearts 心／$flowers 株花。

See README.md for installation, flight controls, the flower quest and wing switching.
安装、飞行操作、花海任务与换翼说明请见 README.md。
"@
    Set-Content -LiteralPath (Join-Path $packageRoot 'BUTTERFLY_WINGS_V1.md') -Value $wingGuide -Encoding UTF8
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath
    python 'tools/check_package.py' $zipPath $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Flight package validation failed.' }
    Write-Output "$Profile package: $zipPath"
} finally {
    Pop-Location
}
