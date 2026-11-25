# Testing

Run `dotnet test` from the repository root. [CI](.github/workflows/build.yml) runs the [MSTest suite](tests/AuthBridge.Tests)
on Linux x64 and Arm64:

- **Unit tests** check CLI argument handling, including each platform's agent help without running command handlers.
  They also cover OAuth redirects and cancellation, helper selection, and update decisions.
- **Integration tests** send a callback through the helper and relay over real loopback sockets and check the
  application's response. They simulate tunnel drops to verify the callback is not replayed. Linux tests also run
  shell setup and upload commands through `sh`.

CI runs two additional kinds of tests:

- **Mutation tests (Linux x64):** Stryker changes selected source files and reruns tests tagged `Mutation` to
  check whether they catch those changes.
- **Package smoke tests (x64 and Arm64):** Linux runs the published client with `--version`. Windows checks the
  executable version, installs and reinstalls the helper, verifies logon and PATH registration, and checks status
  and graceful shutdown.

On Linux x64, CI also uses test coverage for the CRAP complexity gate and checks Roslyn code metrics separately.
See [AGENTS.md](AGENTS.md#build-and-test) for commands and thresholds.

**Manual end-to-end check:** With a live SSH tunnel and a Linux callback listener, run `authbridge --dry-run URL`
on Linux, then request the redirect URI on Windows. The response should come from the Linux listener. CI does not
perform an interactive sign-in.
