# AGENTS.md

## Project overview

Wakeboard is a Windows Wake-on-LAN dashboard delivered as one self-contained `win-x64` executable. A React/Vite UI is compiled and embedded into an ASP.NET Core server. The same process provides authentication, JSON persistence, native Windows adapter discovery, UDP magic-packet transmission, ICMP checks, and Windows service lifecycle commands.

This repository uses Docker as the reproducible build environment. Docker is a build tool here, not part of the installed architecture.

## Source map

- `ui/`: React UI. Browser code must call only same-origin `/api` routes.
- `src/Wakeboard.Server/Program.cs`: application startup, middleware, routes, and embedded-static-file hosting.
- `src/Wakeboard.Server/ServiceCommands.cs`: `install`, `update`, `status`, and `uninstall` behavior.
- `src/Wakeboard.Server/ConfigStore.cs`: versioned JSON storage and atomic mutations.
- `src/Wakeboard.Server/AuthService.cs`: password/session verification and origin checks.
- `src/Wakeboard.Server/NetworkService.cs`: Windows adapter filtering, directed broadcasts, WoL, and ICMP.
- `tests/Wakeboard.Tests/`: unit tests for core security, validation, packet, broadcast, and persistence behavior.
- `build/Dockerfile` and `scripts/build.ps1`: test and packaging pipeline.

## Architectural invariants

- Keep runtime networking in the native Windows process. Do not introduce a helper service or runtime container layer.
- Keep the browser free of credentials, password hashes, secrets, filesystem access, and direct networking privileges.
- Bind the HTTP server according to the configured port and rely on the installer-managed Private-profile firewall rule for LAN access.
- Protect all non-login/session API routes with the signed session cookie.
- Require a valid same-origin `Origin` header for `POST`, `PUT`, and `DELETE` requests.
- Store only the PBKDF2 password hash. Never log or return password or session-secret material.
- Preserve the 12-hour HTTP-only, same-site session and login throttling unless a change explicitly requires different security behavior.
- Treat a successful wake call as packet transmission only. Never claim that a target booted without a separate positive signal.
- Treat ICMP failure as inconclusive. UI and API language must not label a non-responsive host as definitively offline.

## Persistence invariants

- Runtime state belongs under `%ProgramData%\Wakeboard`; repository `data/config.json` is only an optional first-install import source.
- Preserve schema version 1 compatibility unless an explicit, tested migration is added.
- Serialize mutations through the in-process gate and replace `config.json` atomically.
- Do not silently reset, repair, or overwrite corrupt or unsupported configuration.
- Support one process per data directory. Do not imply that the JSON store is safe for multiple replicas.
- Never remove host data unless `uninstall --purge` is explicit. `update` and normal `uninstall` must also preserve settings; reinstall may rotate the password hash and session secret.
- Do not commit `.env`, `data/config.json`, build output, test output, or UI dependency/build directories.

## Networking invariants

- List only active, non-loopback adapters with usable IPv4 address/mask pairs.
- Resolve adapter state and addresses again at wake time rather than trusting stale UI data.
- Calculate directed broadcasts from the selected address and mask, bind to the selected local address, enable UDP broadcast, and use port 9.
- Preserve the standard 102-byte magic-packet format and three-send behavior per eligible IPv4 address.
- The saved adapter is the default; a request may override it without changing the saved host.
- Reachability targets may be hostnames, IPv4, or IPv6, and ICMP checks use a 1.5-second timeout.

## Build and verification

Run the complete supported pipeline from the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1
```

This runs `npm ci`, TypeScript compilation, the Vite production build, `dotnet test`, and the self-contained Windows publish. The expected artifact is `artifacts\win-x64\Wakeboard.exe`.

Pass `-Version <semantic-version>` to stamp a release version. It flows to the UI as the `VITE_WAKEBOARD_VERSION` Docker build argument and to `dotnet publish` as `/p:Version`, so the UI footer and the executable metadata always report the same build. Omitting it yields `dev` in the footer and `0.0.0` in the executable. CI derives the value from the pushed `vX.Y.Z` tag; do not hard-code a version in source. A tagged run also publishes a GitHub release with `Wakeboard.exe` attached, so the `test-and-build` job holds `contents: write` while the workflow default stays `contents: read`.

For focused UI work with Node 24 available:

```powershell
Set-Location ui
npm ci
npm run lint
npm run build
```

For focused backend tests with the .NET 10 SDK available:

```powershell
dotnet test .\tests\Wakeboard.Tests\Wakeboard.Tests.csproj
```

Before handing off changes:

1. Run `git diff --check`.
2. Run focused checks for the affected layer.
3. Run `scripts/build.ps1` for changes affecting packaging, embedded assets, service lifecycle, networking, authentication, persistence, or dependencies.
4. Keep generated `artifacts`, `ui/dist`, `ui/node_modules`, `.next`, and test results out of commits.

## Editing guidance

- Target Windows and `win-x64`; avoid cross-platform abstractions that weaken Windows adapter selection.
- Validate all browser input on the server even when the UI also validates it.
- Keep error responses specific enough to troubleshoot but free of secrets.
- Preserve cancellation tokens on filesystem, ICMP, UDP, and request operations.
- Keep dependencies pinned and update `ui/package-lock.json` with UI dependency changes.
- Update `README.md` and tests whenever user-visible commands, storage, security, ports, packet behavior, or requirements change.
- Installation changes must remain idempotent enough for reinstall/password reset and must not remove host data unless `--purge` is explicit.
