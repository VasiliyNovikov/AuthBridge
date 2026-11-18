#!/usr/bin/env pwsh
# Publishes self-contained, single-file binaries and release packages to ./artifacts.
param([string]$Version = '0.0.0-dev')
$ErrorActionPreference = 'Stop'
$artifacts = Join-Path $PSScriptRoot artifacts
$staging = Join-Path $artifacts staging
Remove-Item $artifacts -Recurse -Force -ErrorAction Ignore

function Publish([string]$Rid) {
    $output = Join-Path $staging $Rid
    dotnet publish "$PSScriptRoot/src/AuthBridge" -c Release -r $Rid -o $output -p:Version=$Version | Out-Host
    if ($LASTEXITCODE) { throw "Publishing $Rid failed." }
    $output
}

# Linux clients, which the Windows packages also carry for 'authbridge install-remote'.
$clients = foreach ($rid in 'linux-x64', 'linux-arm64') {
    $client = Join-Path $artifacts "authbridge-$rid"
    Copy-Item (Join-Path (Publish $rid) authbridge) $client
    $client
}

foreach ($rid in 'win-x64', 'win-arm64') {
    $output = Publish $rid
    # authbridgew.exe is the same program marked as a GUI app (PE subsystem 2), so the helper runs without a console window.
    $exe = [IO.File]::ReadAllBytes("$output/authbridge.exe")
    $exe[[BitConverter]::ToInt32($exe, 0x3C) + 0x5C] = 2
    [IO.File]::WriteAllBytes("$output/authbridgew.exe", $exe)
    Copy-Item ($clients + "$PSScriptRoot/README.md" + "$PSScriptRoot/LICENSE") $output
    Compress-Archive "$output/*" (Join-Path $artifacts "authbridge-$rid.zip")
}
Remove-Item $staging -Recurse -Force
