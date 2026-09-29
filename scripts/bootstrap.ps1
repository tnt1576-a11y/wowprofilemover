$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$sdkExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $sdkExe) { & $sdkExe --version; exit $LASTEXITCODE }
$sdkVersion = (Get-Content -Raw -LiteralPath 'global.json' | ConvertFrom-Json).sdk.version
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$releaseSdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $sdkVersion }) | Select-Object -First 1
if (-not $releaseSdk) { throw ('SDK metadata not found for ' + $sdkVersion) }
$archiveMetadata = $releaseSdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $archiveMetadata) { throw 'Microsoft SDK metadata has no Windows x64 ZIP.' }
New-Item -ItemType Directory -Force -Path '.tools' | Out-Null
$sdkArchive = Join-Path $projectRoot '.tools\dotnet-sdk.zip'
Invoke-WebRequest -Uri $archiveMetadata.url -OutFile $sdkArchive -UseBasicParsing
if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash -ne $archiveMetadata.hash) { throw 'SDK checksum verification failed.' }
Expand-Archive -LiteralPath $sdkArchive -DestinationPath '.tools\dotnet' -Force
& $sdkExe --version
if ($LASTEXITCODE -ne 0) { throw 'The downloaded SDK could not start.' }
