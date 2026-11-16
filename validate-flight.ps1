param(
    [string]$GamePath = 'D:\steam\steamapps\common\Stardew Valley',
    [ValidateSet('QuickTest', 'Formal')][string]$Profile = 'QuickTest'
)
$ErrorActionPreference = 'Stop'
$sdkPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdkPath)) { $sdkPath = (Get-Command dotnet -ErrorAction Stop).Source }
$runtimePath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $runtimePath)) { throw 'A system .NET 6 runtime is required for game assembly checks.' }
Push-Location $PSScriptRoot
try {
    $outputFolder = if ($Profile -eq 'Formal') { 'bin/Release/formal/net6.0' } else { 'bin/Release/net6.0' }
    & $sdkPath build 'tests/FlightChecks/FlightChecks.csproj' -c Release "-p:GamePath=$GamePath" "-p:AngelWingsProfile=$Profile" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Flight checks build failed.' }
    & $runtimePath (Join-Path "tests/FlightChecks/$outputFolder" 'FlightChecks.dll') $GamePath
    if ($LASTEXITCODE -ne 0) { throw 'Flight behavior checks failed.' }
    & $sdkPath build 'tools/inspect/GameSignatures.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Game binding check build failed.' }
    $modPath = Join-Path $PSScriptRoot "src/AngelWings.VisualPreview/$outputFolder/AngelWings.VisualPreview.dll"
    & $runtimePath 'tools/inspect/bin/Release/net6.0/GameSignatures.dll' $GamePath $modPath '--quest-profile' $Profile
    if ($LASTEXITCODE -ne 0) { throw 'Compiled quest profile check failed.' }
    & $runtimePath 'tools/inspect/bin/Release/net6.0/GameSignatures.dll' $GamePath $modPath
    if ($LASTEXITCODE -ne 0) { throw 'Game method binding check failed.' }
} finally { Pop-Location }
