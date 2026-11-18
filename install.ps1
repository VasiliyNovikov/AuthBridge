# Installs AuthBridge for the current user, or updates it, and starts its helper. Run in PowerShell:
#   irm https://raw.githubusercontent.com/VasiliyNovikov/AuthBridge/main/install.ps1 | iex
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $folder = Join-Path $env:LOCALAPPDATA 'Programs\AuthBridge'
    $exe = Join-Path $folder 'authbridge.exe'
    $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $zip = Join-Path ([IO.Path]::GetTempPath()) "authbridge-$PID.zip"
    Invoke-WebRequest "https://github.com/VasiliyNovikov/AuthBridge/releases/latest/download/authbridge-win-$architecture.zip" -OutFile $zip -UseBasicParsing
    try {
        if (Test-Path $exe) { & $exe stop }
        # The files of a helper that has just stopped can stay in use for a moment.
        for ($attempt = 1; ; $attempt++) {
            try { Expand-Archive $zip $folder -Force; break }
            catch { if ($attempt -ge 10) { throw }; Start-Sleep 2 }
        }
    }
    finally {
        Remove-Item $zip
    }
    try { & $exe install }
    catch {
        # Microsoft Defender attack surface reduction rules can block Windows PowerShell from starting a program it has
        # just downloaded.
        $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        throw "Cannot start $exe ($reason). If Microsoft Defender blocked it, run this command in PowerShell 7 (pwsh)."
    }
    # The user PATH applies to terminals started from now on; this one gets it too.
    if (($env:Path -split ';') -notcontains $folder) { $env:Path += ";$folder" }
    Write-Host 'Next, connect each Linux machine: authbridge install-remote NAME [SSH OPTIONS...] DESTINATION'
}
