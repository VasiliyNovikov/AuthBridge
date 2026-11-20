# AGENTS.md

Guidance for AI agents and contributors working on this repository. User documentation is in `README.md`.

## Build and test

Requires the .NET 11 SDK (pinned in `global.json`).

```shell
dotnet test              # builds and runs the tests (MSTest on Microsoft.Testing.Platform)
dotnet crap.cs           # runs the Release tests with coverage and checks the whole-app CRAP load
dotnet coupling.cs       # reports and gates all six Roslyn code metrics
dotnet release.cs 1.2.3  # self-contained, single-file, trimmed binaries and release zips in ./artifacts
```

Warnings are errors, including trimming warnings. CI (`.github/workflows/build.yml`) runs unit tests on x64 and Arm64
Linux and builds all packages on Linux with `release.cs`. Each package runs on its native architecture: the Linux
clients print their version, and the Windows runners check the executable's version resource, install the helper,
repeat installation in place, verify its logon command and user PATH entry, and stop it. For a `vX.Y.Z` tag, CI
publishes those same packages as a GitHub release.

`code-metrics-baseline.props` is the single source for the Roslyn, CRAP and mutation gate thresholds.

### CRAP baseline

The Ubuntu 24.04 x64 test job runs `dotnet crap.cs` instead of running the tests twice. The script restores the
repo-local Crap4DotNet tool and analyzes `src/AuthBridge` (not tests, build scripts, `bin`, or `obj`) using Cobertura
coverage from Coverlet's MTP extension. Crap4DotNet prefers branch coverage and falls back to line coverage when
branch data is unavailable. Compiler changes such as runtime async can therefore change the measured load without
changing source complexity. The `crap-analysis` CI artifact and local `artifacts/crap/` directory contain `report.json`
with the method scores and matching warnings, and `coverage.cobertura.xml`.

The gate compares **total CRAP load**, not the sum or average of the method scores. A method's score is
`complexity^2 * (1 - coverage)^3 + complexity`, where coverage is a fraction. Only methods scoring **above**
`crapMethodThreshold` contribute load: `complexity * (1 - coverage) + complexity / threshold`. CI fails if their
combined load, rounded to three decimal places, exceeds `maxTotalCrapLoad`. Improvements can offset regressions
elsewhere, so use the method report to see what changed.

To tighten the ceiling after an improvement, copy the new Linux x64 load into the baseline in a reviewed change.
CI never rewrites it. Other platforms can run the same command, but their test coverage can differ.

### Roslyn code metrics

The production build and the CI report use the same Roslyn limits, initially set to the observed extremes on commit
`a693d99`:

| Scope | Metric | Initial passing limit | `dotnet build` rule |
| --- | --- | ---: | --- |
| Type | Class coupling (distinct referenced types, including library types) | at most 56 | CA1506 |
| Type | Depth of inheritance | at most 4 | CA1501 |
| Method | Maintainability index | at least 46 | CA1505 |
| Method | Cyclomatic complexity | at most 14 | CA1502 |
| Method | Source lines (including blank lines) | at most 70 | CI only |
| Method | Executable lines (Roslyn's estimate from operations) | at most 28 | CI only |

`src/AuthBridge/.globalconfig` enables the four SDK analyzers for the production project. MSBuild writes their
limits to `CodeMetricsConfig.txt` under `obj` and passes it to the compiler as an additional file. Do not edit that
generated file. A diagnostic over a ceiling or below the floor fails the build because warnings are errors. The
project-wide config also enables CA1509 so malformed analyzer thresholds fail the build rather than disabling the
checks. The test project and build scripts do not opt in. An ordinary build does not run the reporting script.

The Ubuntu x64 job runs `dotnet coupling.cs` to check all six [Roslyn metrics](https://learn.microsoft.com/visualstudio/code-quality/code-metrics-values)
in one Release compilation and produce the report. The build and report share thresholds, but the SDK does not
diagnose generated source and CA1501 excludes `System` namespaces from the inheritance chain. The report retains
the full depth and counts generated members of partial types (notably the `Json` serialization context).
Its method limits apply to methods, constructors and accessors with their own declaration in project source,
excluding source-generator output and primary constructors, for which Roslyn reports the whole type's source span.

Equality passes. Any worse extreme fails CI and blocks tagged releases; adding more types or methods is not itself a
failure. The log names a worst symbol for each metric. The `class-coupling` artifact and local `artifacts/coupling/`
contain `report.json` (per-type dependencies and per-method measurements) and `observed.json` (the six extremes,
keyed by baseline property name), including when a limit is crossed. To tighten a limit, copy its improved value
from `observed.json` into `code-metrics-baseline.props` in a reviewed change. CI never rewrites the baseline.

### Mutation testing

From `tests/AuthBridge.Tests`, run `dotnet tool restore` and `dotnet stryker`. `stryker-config.json` lists individual
source files relative to `src/AuthBridge`. The Microsoft.Testing.Platform runner selects tests marked
`[TestCategory("Mutation")]`. Open `StrykerOutput/<run>/reports/mutation-report.html` to see which changes the
tests missed; the JSON report is in the same directory. To reproduce the POSIX quoting result, run on Linux:
the test passes the quoted path through a real `sh` and is skipped on other platforms.

The relay target exercises `ForwardAsync`. Stryker currently rolls back the mutations in `Relay.RunAsync` after a
compile error, so the relay score does not measure its polling and reconnect loop.

The `mutation` job in `build.yml` runs on Ubuntu 24.04 x64 for pull requests, pushes to `main`, and `v*` tags.
It uploads both reports as `mutation-reports`. A missing report, a target with no killed mutants, or an **overall
mutation score** below the `detectedMutants / validMutants` baseline fails the build and blocks tagged releases.
There are no per-file score floors. To check a report from the repo root, run `dotnet check-mutation-score.cs <mutation-report.json>`.

The gate compares the exact fraction of detected mutants (killed or timed out) among valid mutants (detected,
survived, or not covered); the displayed percentages are rounded only for readability. The initial floor is
102/167 (61.08%), the lower of the [main](https://github.com/VasiliyNovikov/AuthBridge/actions/runs/44584029259)
and [tag](https://github.com/VasiliyNovikov/AuthBridge/actions/runs/44584029415) runs of the same `v0.1.21` commit.
To raise the floor, update both counts in a reviewed change using repeatable Linux x64 results. CI never rewrites them.

## Layout

One executable, `authbridge`; `Program.cs` runs the Windows or the Linux commands depending on the OS.

- `src/AuthBridge/Host/`: `HostCommands` handles the Windows CLI; `HelperHost` runs the control API (`Helper`),
  loopback relay sessions (`RelaySession`), reverse SSH tunnels (`Tunnel`), and notification area icon (`TrayIcon`).
  `AboutDialog` owns its dialog resources, `RemoteClient` manages the client on a remote machine, `GitHubReleases`
  fetches releases, and `InstalledPackage` replaces the local files.
- `src/AuthBridge/Client/`: the Linux commands (`ClientCommands`), browser requests and background worker handoff
  (`BrowserClient`), tunnel socket discovery (`Bridge`), forwarding to the waiting application (`Relay`), and the
  shell, Git Credential Manager and NuGet setup and wrappers.
- `src/AuthBridge/Common/`: minimal HTTP/1.1 server and messages, control API protocol and client, OAuth URL helpers,
  release version information (`AppVersion`), logging and paths.
- `tests/AuthBridge.Tests/`: a few focused tests, including an in-process relay round trip (`RelayTests`).
- `install.ps1`: the installer behind `irm ... | iex`. It only downloads the latest release and runs its
  `authbridge install`, and it must also run in Windows PowerShell 5.1.

## Conventions

- Write code that both human contributors and AI agents can understand and maintain. Use descriptive names, keep
  related behavior together, and make control flow easy to follow.
- Apply SOLID principles pragmatically: give each component a clear responsibility and make its dependencies explicit.
  Add an interface or layer only when it makes a concrete change easier to implement or test.
- Use System.CommandLine for the CLI and otherwise use only the base class library. Serialize through the
  source-generated `Json` context in `Common/Protocol.cs`, and register new types there.
- Bump `Protocol.Version` for incompatible control API changes; both machines must run the same version. When a tunnel
  first connects, the helper replaces an older client with the one in its package, and never downgrades one, so that
  helpers of different versions do not undo each other's updates.
- Keep tests few and focused; extend the existing test classes.
- Text files use LF line endings (`.gitattributes`).

## Gotchas

- Windows children inherit every inheritable handle. `HostCommands.LaunchHelper` clears the inherit flag on the standard
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
  it can rename one. `InstalledPackage` therefore writes each new file beside the old one, renames the old one to
  `*.authbridge-old`, and moves the new one into place; the helper deletes the old files when it starts.
- Microsoft Defender attack surface reduction rules on managed devices can block Windows PowerShell 5.1 from starting
  an executable that it has just downloaded or extracted ("Access is denied"); PowerShell 7 was not blocked in
  testing. The docs therefore recommend PowerShell 7 for `install.ps1`.
- Applications such as MSAL wait for their browser command to exit before they serve the redirect, so the client relays
  in a detached background process (`__relay`) that does not hold the caller's standard streams.
- Each tunnel connection gets a new remote socket, so a relay whose tunnel dropped finds its helper again, by machine
  name and process id, through the reconnect callback of `Relay.RunAsync`; the helper keeps the session meanwhile.
- With several Windows machines connected to one Linux machine, `Bridge` pings every tunnel socket at once and prefers
  the helper whose PC had keyboard or mouse input most recently (`GetLastInputInfo`), so that sign-ins open where the
  user is. The tunnel of a PC that has gone to sleep can accept connections without answering, so once one helper has
  answered, the others get only a second more.
- When the helper cannot relay a redirect (not connected, or HTTP 409 because Windows reserves the port), the client
  sends the waiting application an OAuth error (`OAuth.CancelRequest`) so that it fails at once.
- `--gcm` and `--nuget-plugin` replace the process via `execvpe`. Git Credential Manager and MSAL.NET offer browser
  sign-in only in desktop sessions, so the wrappers set a placeholder `WAYLAND_DISPLAY` or `DISPLAY` while the helper is
  connected.
- On Linux, the state directory comes from the passwd entry rather than `$HOME`, which tools sometimes override.
- For manual end-to-end tests, `authbridge --dry-run URL` on Linux sets up a relay without opening a browser; requests
  to the URL's `redirect_uri` port on Windows then reach that port on Linux.
