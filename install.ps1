# Downloads the latest AuthBridge release and runs its 'authbridge install', which installs it for the current user.
# Run in PowerShell:
#   irm https://raw.githubusercontent.com/VasiliyNovikov/AuthBridge/main/install.ps1 | iex
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $package = Join-Path ([IO.Path]::GetTempPath()) "authbridge-$PID"
    try {
        Invoke-WebRequest "https://github.com/VasiliyNovikov/AuthBridge/releases/latest/download/authbridge-win-$architecture.zip" -OutFile "$package.zip" -UseBasicParsing
        Expand-Archive "$package.zip" $package -Force
        try { & "$package\authbridge.exe" install }
        catch {
            # Microsoft Defender attack surface reduction rules can block Windows PowerShell from starting a program it
            # has just downloaded.
            $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
            throw "Cannot start authbridge.exe ($reason). If Microsoft Defender blocked it, run this command in PowerShell 7 (pwsh)."
        }
    }
    finally {
        Remove-Item "$package.zip", $package -Recurse -Force -ErrorAction SilentlyContinue
    }
}
