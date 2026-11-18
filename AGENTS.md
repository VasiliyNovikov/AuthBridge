# AGENTS.md

Guidance for AI agents and contributors working on this repository. User documentation is in `README.md`.

## Build and test

Requires the .NET 11 SDK (pinned in `global.json`).

```shell
dotnet test              # builds and runs the tests (MSTest on Microsoft.Testing.Platform)
dotnet release.cs 1.2.3  # self-contained, single-file, trimmed binaries and release zips in ./artifacts
```

Warnings are errors, including trimming warnings. Pushing a `vX.Y.Z` tag makes CI (`.github/workflows/build.yml`)
run `release.cs` and create a GitHub release with its artifacts.

## Layout

One executable, `authbridge`; `Program.cs` runs the Windows or the Linux commands depending on the OS.

- `src/AuthBridge/Host/`: the Windows helper. Control API (`Helper`), relay sessions listening on loopback ports
  (`RelaySession`), reverse SSH tunnels (`Tunnel`), the notification area icon (`TrayIcon`), the client on remote
  machines (`RemoteClient`), updates from GitHub releases (`Updater`), and the Windows commands (`HostCommands`).
- `src/AuthBridge/Client/`: the Linux client. The `BROWSER` entry point and commands (`ClientCommands`), tunnel socket
  discovery (`Bridge`), forwarding of relayed requests to the waiting application (`Relay`), and the shell, Git
  Credential Manager and NuGet setup and wrappers.
- `src/AuthBridge/Common/`: minimal HTTP/1.1 server and messages, control API protocol and client, OAuth URL helpers,
  logging and paths.
- `tests/AuthBridge.Tests/`: a few focused tests, including an in-process relay round trip (`RelayTests`).

## Conventions

- Use only the base class library. Serialize through the source-generated `Json` context in `Common/Protocol.cs`, and
  register new types there.
- Bump `Protocol.Version` for incompatible control API changes; both machines must run the same version. When a tunnel
  first connects, the helper replaces an older client with the one in its package, and never downgrades one, so that
  helpers of different versions do not undo each other's updates.
- Keep tests few and focused; extend the existing test classes.
- Text files use LF line endings (`.gitattributes`).

## Gotchas

- Windows children inherit every inheritable handle. `HostCommands.StartAsync` clears the inherit flag on the standard
  handles before it starts the helper; otherwise `authbridge start` never finishes when its output is redirected.
- `release.cs` derives `authbridgew.exe` from `authbridge.exe` by setting the PE subsystem to GUI, so the helper
  started at logon has no console window.
- `TrayIcon` uses raw Win32 because Windows Forms does not support trimming. Its window is hidden rather than
  message-only, so that it receives `TaskbarCreated` after Explorer restarts. Closing the window stops the helper;
  `authbridge stop` does that rather than killing the process, which would leave a stale icon.
- Windows 11 keeps an icon's first tooltip as its name, so `TrayIcon` adds the icon with the tooltip `AuthBridge`
  before it sets the status.
- `AuthBridge.ico` is the executable's icon and, as an embedded resource, the base of the tray icon, which draws the
  status dot onto its pixels. Keep its images below 256 pixels as uncompressed 32-bit bitmaps.
- Windows cannot overwrite or delete a running or memory-mapped executable, even shortly after its process exits, but
  it can rename one. `Updater.Install` therefore renames each replaced file to `*.authbridge-old`, and the helper
  deletes those when it starts.
- Applications such as MSAL wait for their browser command to exit before they serve the redirect, so the client relays
  in a detached background process (`__relay`) that does not hold the caller's standard streams.
- Each tunnel connection gets a new remote socket, so a relay whose tunnel dropped finds the helper again through the
  reconnect callback of `Relay.RunAsync`; the helper keeps the session meanwhile.
- When the helper cannot relay a redirect (not connected, or HTTP 409 because Windows reserves the port), the client
  sends the waiting application an OAuth error (`OAuth.CancelRequest`) so that it fails at once.
- `--gcm` and `--nuget-plugin` replace the process via `execvpe`. Git Credential Manager and MSAL.NET offer browser
  sign-in only in desktop sessions, so the wrappers set a placeholder `WAYLAND_DISPLAY` or `DISPLAY` while the helper is
  connected.
- On Linux, the state directory comes from the passwd entry rather than `$HOME`, which tools sometimes override.
- For manual end-to-end tests, `authbridge --dry-run URL` on Linux sets up a relay without opening a browser; requests
  to the URL's `redirect_uri` port on Windows then reach that port on Linux.
