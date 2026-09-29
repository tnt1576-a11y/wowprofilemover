param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
if (-not $SkipTests) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Validation failed; publish stopped.' }
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExe)) { $sdkExe = (Get-Command dotnet -ErrorAction Stop).Source }
$releaseName = 'WoWProfileMover-0.1.0-win-x64'
$publishDirectory = Join-Path $projectRoot ('dist\' + $releaseName)
& $sdkExe publish 'src\WowProfileMover.App\WowProfileMover.App.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $publishDirectory 'README.md')
Copy-Item -LiteralPath 'companion\README.md' -Destination (Join-Path $publishDirectory 'COMPANION-GUIDE.md')
$zipPath = Join-Path $projectRoot ('dist\' + $releaseName + '.zip')
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($zipPath + '.sha256', $hash + '  ' + [System.IO.Path]::GetFileName($zipPath) + [Environment]::NewLine)
Write-Output ('Release: ' + $zipPath)
