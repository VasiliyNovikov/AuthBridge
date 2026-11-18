# AuthBridge

Sign in on remote Linux machines with your local Windows browser.

Command-line tools that sign in through a browser — `az login`, Git Credential Manager, the Azure Artifacts
credential provider, or anything else that uses an OAuth loopback redirect to `http://localhost:PORT` — cannot
do it on a headless Linux machine you reach over SSH. AuthBridge opens their URLs in your Windows browser,
where you are already signed in, and relays the redirect back to the Linux machine.

## How it works

- A helper on Windows keeps an SSH connection to each Linux machine, with a reverse tunnel from a private Unix
  socket in `~/.authbridge` to the helper's control port.
- On Linux, `BROWSER` points to `authbridge`, which passes URLs through the tunnel to the helper.
- When a URL redirects to `http://localhost:PORT`, the helper listens on that port on Windows and relays the
  browser's redirect to the application waiting on Linux.
- When the redirect cannot be relayed (no helper connected, or Windows reserves the port), the application
  receives an OAuth error at once instead of waiting.

## Install

Requirements: Windows with the OpenSSH client, and key-based SSH access to the Linux machine
(`ssh -o BatchMode=yes <host> true` must succeed). Linux machines can be x64 or arm64.

1. Download `authbridge-win-x64.zip` (or `-win-arm64`) from
   [Releases](https://github.com/VasiliyNovikov/AuthBridge/releases) and extract it:

   ```powershell
   Unblock-File authbridge-win-x64.zip
   Expand-Archive authbridge-win-x64.zip $env:LOCALAPPDATA\Programs\AuthBridge
   cd $env:LOCALAPPDATA\Programs\AuthBridge
   ```

2. Connect a Linux machine (`devbox` is a name of your choice; the remaining arguments go to `ssh`), and let the
   helper start at logon:

   ```powershell
   .\authbridge install-remote devbox -p 2222 user@devbox.example.com
   .\authbridge install-startup
   ```

   This copies the client to `~/.local/bin/authbridge`, exports `BROWSER` in `~/.profile` and `~/.bashrc`, and
   starts the helper. Repeat it for more machines.

3. Open a new SSH session on the Linux machine. Browser sign-ins now open in your Windows browser.

Optional integrations, run on the Linux machine:

| Command | Effect |
| --- | --- |
| `authbridge --setup-gcm` | Git Credential Manager signs in with the browser instead of a device code. |
| `authbridge --setup-nuget` | The Azure Artifacts credential provider (`dotnet restore --interactive`) does the same. `~/.local/bin` must be on `PATH`. |

## Usage

| Windows | |
| --- | --- |
| `authbridge install-remote NAME [SSH OPTIONS...] DESTINATION` | Install the client on a Linux machine and keep a tunnel to it. |
| `authbridge install-startup` | Start the helper at logon. |
| `authbridge start`, `stop`, `status` | Control the background helper; `serve` runs it in the foreground. |

| Linux | |
| --- | --- |
| `authbridge URL...` | Open URLs in the Windows browser (used through `BROWSER`). |
| `authbridge --status` | Check the connection to the Windows helper. |
| `authbridge --setup-shell`, `--setup-gcm`, `--setup-nuget` | Configure the shell and integrations. |

Logs are in `%USERPROFILE%\.authbridge\host.log` and `~/.authbridge/client.log`. Tunnels are kept in
`%USERPROFILE%\.authbridge\config.json`, which also accepts a browser command such as
`"browserCommand": ["C:\\Program Files\\Mozilla Firefox\\firefox.exe", "{url}"]`.

## Notes

- Windows reserves some port ranges, for example for Hyper-V. If an application picked such a port, its sign-in
  fails at once and succeeds on retry.
- For SSH hosts with a `RemoteCommand` in `~/.ssh/config`, add `-o RemoteCommand=none` to `install-remote`.
- When several Windows machines connect to one Linux machine, URLs open on the most recently connected one.
- The control API listens on `127.0.0.1` only, and requires a custom header that web pages cannot send.

## Build

With the .NET 11 SDK:

```shell
dotnet test
./publish.ps1 -Version 1.0.0   # self-contained, single-file binaries and packages in ./artifacts
```
