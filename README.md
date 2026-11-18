# AuthBridge

Sign in on remote Linux machines with your local Windows browser.

> [!NOTE]
> This project is 100% vibe-coded: an AI coding agent wrote all of its code and documentation, directed by a human.
> Review it before you rely on it.

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

### With an AI agent

Copy this prompt into a coding agent running on your Windows machine (for example GitHub Copilot CLI), and fill in
how you reach your Linux machines:

```text
Set up AuthBridge (https://github.com/VasiliyNovikov/AuthBridge) so that browser sign-ins on my Linux machines open
in my Windows browser. I reach each Linux machine with these ssh arguments, one machine per line:
<OPTIONS AND DESTINATION, e.g. -p 2222 user@devbox>
If they are still a placeholder, ask me for them. Run .\authbridge commands in %LOCALAPPDATA%\Programs\AuthBridge.
Do step 2 once, and the other steps for each machine.

1. If `ssh -G <the ssh arguments>` prints a remotecommand line, add `-o RemoteCommand=none` to the ssh arguments.
   Then check that `ssh -o BatchMode=yes <the ssh arguments> true` succeeds; if it does not, stop and explain how
   to set up key-based SSH access.
2. Install AuthBridge, or update it, by running this in PowerShell 7 (pwsh) if it is installed, or else in Windows
   PowerShell:
   irm https://raw.githubusercontent.com/VasiliyNovikov/AuthBridge/main/install.ps1 | iex
3. Run `.\authbridge install-remote <name> <the ssh arguments>`, where <name> is this machine's name in
   %USERPROFILE%\.authbridge\config.json if it is already there, or else a new short name.
4. Run `.\authbridge remote <name> --setup-all`. It sets up the shell, and Git Credential Manager and the Azure
   Artifacts credential provider if they are installed (NuGet then needs ~/.local/bin on PATH, which Ubuntu's default
   ~/.profile provides).
5. Confirm that `.\authbridge status` shows the tunnel as connected and that `.\authbridge remote <name> --status`
   succeeds; if not, check %USERPROFILE%\.authbridge\host.log. Then tell me to open a new SSH session, or to run
   `eval "$(~/.local/bin/authbridge --env)"` in sessions that are already open.
```

### With PowerShell

1. Download the latest release and run its `authbridge install`, which installs it in
   `%LOCALAPPDATA%\Programs\AuthBridge`, adds it to your `PATH`, and starts the helper now and at logon. Use PowerShell 7
   (`pwsh`) if you have it: on managed devices, Microsoft Defender can block Windows PowerShell 5.1 from starting
   programs that it has just downloaded.

   ```powershell
   irm https://raw.githubusercontent.com/VasiliyNovikov/AuthBridge/main/install.ps1 | iex
   ```

2. In a new terminal, connect a Linux machine (`devbox` is a name of your choice; the remaining arguments go to `ssh`):

   ```powershell
   authbridge install-remote devbox -p 2222 user@devbox.example.com
   ```

   `install-remote` copies the client to `~/.local/bin/authbridge` and exports `BROWSER` in `~/.profile` and
   `~/.bashrc`. Repeat it for more machines.

3. Open a new SSH session on the Linux machine, or run `eval "$(~/.local/bin/authbridge --env)"` in one that is
   already open. Browser sign-ins now open in your Windows browser.

To install without the script, extract `authbridge-win-x64.zip` (`authbridge-win-arm64.zip` on ARM64 Windows) from the
[latest release](https://github.com/VasiliyNovikov/AuthBridge/releases/latest) to any folder, and run
`.\authbridge install` there.

Optional integrations, run on the Linux machine, or from Windows as `authbridge remote devbox --setup-gcm` and so on.
`--setup-all` sets up the shell and each integration whose tool is installed.

| Command | Effect |
| --- | --- |
| `authbridge --setup-gcm` | Git Credential Manager signs in with the browser instead of a device code. |
| `authbridge --setup-nuget` | The Azure Artifacts credential provider (`dotnet restore --interactive`) does the same. `~/.local/bin` must be on `PATH`. |

## Usage

| Windows | |
| --- | --- |
| `authbridge install` | Install in `%LOCALAPPDATA%\Programs\AuthBridge`, start the helper now and at logon, and add `authbridge` to your `PATH`. |
| `authbridge install-remote NAME [SSH OPTIONS...] DESTINATION` | Install the client on a Linux machine and keep a tunnel to it. |
| `authbridge remote NAME ARGS...` | Run the Linux client on that machine, for example `authbridge remote devbox --setup-gcm`. |
| `authbridge update` | Install the latest release; each Linux machine gets the matching client when its tunnel connects. |
| `authbridge start`, `stop`, `status` | Control the background helper; `serve` runs it in the foreground. |

| Linux | |
| --- | --- |
| `authbridge URL...` | Open URLs in the Windows browser (used through `BROWSER`). |
| `authbridge --status` | Check the connection to the Windows helper. |
| `authbridge --env` | Print the command that sets `BROWSER`; `eval "$(authbridge --env)"` sets it in a shell that was already open. |
| `authbridge --setup-shell`, `--setup-gcm`, `--setup-nuget`, `--setup-all` | Configure the shell and integrations. |

On Windows, the dot on the helper's notification area icon is green when all machines are connected, yellow when some
are, and gray when none are. Hover over it for each machine's state, or right-click it to open the log or exit.
Windows 11 may first put it among the hidden icons; drag it to the taskbar to keep it in view.

Logs are in `%USERPROFILE%\.authbridge\host.log` and `~/.authbridge/client.log`. Tunnels are kept in
`%USERPROFILE%\.authbridge\config.json`, which also accepts a browser command such as
`"browserCommand": ["C:\\Program Files\\Mozilla Firefox\\firefox.exe", "{url}"]`.

## Notes

- To upgrade, run `authbridge update`. The helper replaces older clients on the Linux machines when their tunnels
  connect, so machines that are offline catch up later; `authbridge status` shows each machine's client version.
  Versions before 0.1.7 have no `update`; run the install command again instead.
- Dropped SSH connections, for example while a VPN or tunnel service is down, reconnect by themselves: the helper
  retries at least once a minute for as long as needed. Sign-ins in progress continue over the new connection; sign-ins
  started while disconnected fail at once.
- Windows reserves some port ranges, for example for Hyper-V. If an application picked such a port, its sign-in
  fails at once and succeeds on retry.
- For SSH hosts with a `RemoteCommand` in `~/.ssh/config`, add `-o RemoteCommand=none` to `install-remote`.
- When several Windows machines connect to one Linux machine, URLs open on the most recently connected one.
- The control API listens on `127.0.0.1` only, and requires a custom header that web pages cannot send.

## Development

See [AGENTS.md](AGENTS.md) for building, testing and the code layout.
