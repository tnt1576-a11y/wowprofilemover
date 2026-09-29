param([switch]$SkipLua)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExe)) { $sdkExe = (Get-Command dotnet -ErrorAction Stop).Source }
& $sdkExe build 'src\WowProfileMover.App\WowProfileMover.App.csproj' -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
& $sdkExe run --project 'tests\WowProfileMover.Tests\WowProfileMover.Tests.csproj' -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed.' }
if (-not $SkipLua) {
    $luaExe = Join-Path $projectRoot '.tools\lua-runtime\node_modules\.bin\fengari.cmd'
    if (-not (Test-Path -LiteralPath $luaExe)) {
        & npm.cmd install --prefix '.tools\lua-runtime' --ignore-scripts --no-audit --no-fund 'fengari-node-cli@0.1.0'
        if ($LASTEXITCODE -ne 0) { throw 'Lua test runtime installation failed.' }
    }
    foreach ($suite in @('core_spec.lua','runtime_spec.lua','ui_smoke_spec.lua')) {
        & $luaExe (Join-Path 'companion\tests' $suite)
        if ($LASTEXITCODE -ne 0) { throw ('Companion test failed: ' + $suite) }
    }
}
New-Item -ItemType Directory -Force -Path '.artifacts' | Out-Null
$smokeRoot = Join-Path $projectRoot ('.artifacts\ui-smoke-' + [Guid]::NewGuid().ToString('N'))
$appDll = Join-Path $projectRoot 'src\WowProfileMover.App\bin\Release\net10.0-windows\WoWProfileMover.dll'
$renderPath = Join-Path $projectRoot '.artifacts\transfer.png'
# Quote individual arguments for Windows paths containing spaces.
$appArguments = @($appDll, '--demo', '--data-root', $smokeRoot, '--render', $renderPath, '--smoke') | ForEach-Object { '"' + $_ + '"' }
$smokeProcess = Start-Process -FilePath $sdkExe -ArgumentList $appArguments -WindowStyle Hidden -PassThru
if (-not $smokeProcess.WaitForExit(45000)) { throw ('UI smoke timed out. Inspect process ' + $smokeProcess.Id) }
if ($smokeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $smokeRoot 'ui-smoke-passed.txt'))) { throw ('UI smoke failed. Inspect ' + $smokeRoot) }
Write-Output 'Desktop and companion validation passed.'
