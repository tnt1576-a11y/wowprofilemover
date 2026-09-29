$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$zipPath = Join-Path $projectRoot 'dist\WoWProfileMover-0.1.0-win-x64.zip'
$expectedHash = (Get-Content -Raw -LiteralPath ($zipPath + '.sha256')).Split(' ')[0].Trim()
if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Release ZIP checksum mismatch.' }
$verificationRoot = Join-Path $projectRoot ('.artifacts\package-check-' + [Guid]::NewGuid().ToString('N'))
$extractionRoot = Join-Path $verificationRoot 'app'
Expand-Archive -LiteralPath $zipPath -DestinationPath $extractionRoot
foreach ($name in @('Core.lua','Runtime.lua','UI.lua','WowProfileMoverBridge.toc')) {
    $sourcePath = Join-Path $projectRoot ('companion\WowProfileMoverBridge\' + $name)
    $packagedPath = Join-Path $extractionRoot ('Companion\WowProfileMoverBridge\' + $name)
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $packagedPath -Algorithm SHA256).Hash) { throw ('Packaged companion mismatch: ' + $name) }
}
$dataRoot = Join-Path $verificationRoot 'data'
$renderPath = Join-Path $projectRoot '.artifacts\release-preview.png'
$appArguments = @('--demo', '--data-root', $dataRoot, '--render', $renderPath, '--smoke') | ForEach-Object { '"' + $_ + '"' }
$appProcess = Start-Process -FilePath (Join-Path $extractionRoot 'WoWProfileMover.exe') -ArgumentList $appArguments -WindowStyle Hidden -PassThru
if (-not $appProcess.WaitForExit(45000)) { throw ('Packaged app timed out. Inspect process ' + $appProcess.Id) }
if ($appProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $dataRoot 'ui-smoke-passed.txt'))) { throw ('Packaged app smoke failed. Inspect ' + $dataRoot) }
Get-Content -LiteralPath (Join-Path $dataRoot 'ui-smoke-passed.txt')
Write-Output ('Packaged executable, companion hashes, and ZIP checksum verified: ' + $verificationRoot)
