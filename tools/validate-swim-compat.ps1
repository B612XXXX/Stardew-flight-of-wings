param(
    [string]$GamePath = 'D:\steam\steamapps\common\Stardew Valley',
    [Parameter(Mandatory = $true)][string]$SwimDllPath,
    [ValidateSet('QuickTest', 'Formal')][string]$Profile = 'Formal'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdkPath = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$runtimePath = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
$swimPath = (Resolve-Path -LiteralPath $SwimDllPath).Path
Push-Location $projectRoot
try {
    & $sdkPath build 'tests/SwimIntegrationChecks/SwimIntegrationChecks.csproj' -c Release "-p:GamePath=$GamePath" "-p:SwimDllPath=$swimPath" "-p:AngelWingsProfile=$Profile" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Swim integration check build failed.' }
    $output = if ($Profile -eq 'Formal') { 'bin/Release/formal/net6.0' } else { 'bin/Release/net6.0' }
    $manifest = Get-Content -LiteralPath 'src/AngelWings.VisualPreview/manifest.json' -Raw | ConvertFrom-Json
    $version = if ($Profile -eq 'Formal') { $manifest.Version -replace '-quick-test$', '' } else { $manifest.Version }
    $report = Join-Path $projectRoot "build/swim-compatibility-$version.json"
    & $runtimePath "tests/SwimIntegrationChecks/$output/SwimIntegrationChecks.dll" $GamePath $report
    if ($LASTEXITCODE -ne 0) { throw 'Swim integration checks failed.' }
    Write-Output "Offline Swim integration report: $report"
} finally { Pop-Location }
