# Wakeboard

Wakeboard is a native Windows Wake-on-LAN dashboard packaged as a single self-contained `Wakeboard.exe`. It combines the responsive web UI, HTTP server, shared-password authentication, host storage, Windows adapter discovery, ICMP reachability checks, and UDP magic-packet sender.

The dashboard lets you:

- Add, edit, and remove wakeable devices.
- Save a preferred Windows network adapter for each device.
- Override the adapter for an individual wake request.
- Check a saved IP address or hostname with ICMP when the page loads or on demand.
- See whether packet transmission succeeded without implying that the target finished booting.

An **Awake** status means the target replied to ICMP. **No ping response** is inconclusive because a running computer may block ICMP.

## Runtime requirements

- 64-bit Windows 10/11 or Windows Server
- Administrator approval for installation, updates, firewall configuration, and removal
- Wake-on-LAN enabled in each target computer's firmware, operating system, and network adapter
- An IPv4 network path that permits broadcasts between the Wakeboard computer and target devices
- A trusted private LAN; Wakeboard is not intended for direct internet exposure

The locally built executable is not code-signed, so Windows may display a reputation or SmartScreen warning.

## Build

Building requires PowerShell and Docker Desktop or Docker Engine running Linux containers. The first build also needs internet access to download the Node and .NET SDK images and package dependencies.

From the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1
```

The script compiles the React UI, runs the .NET tests, cross-publishes a self-contained `win-x64` application, and writes:

```text
artifacts\win-x64\Wakeboard.exe
```

The output directory is replaced on each build. Use `-Output <relative-path>` to select a different directory inside the repository.

Use `-Version <semantic-version>` to stamp a version into the build, for example `.\scripts\build.ps1 -Version 0.0.3`. The value is shown in the footer of every page in the web UI and written to the executable's file and product version metadata. Builds without `-Version` report `dev` in the footer and `0.0.0` in the executable metadata.

Tagged releases are versioned automatically. Pushing a `vX.Y.Z` tag makes CI run `scripts/build.ps1` with that version, upload the executable as the `Wakeboard-win-x64-vX.Y.Z` workflow artifact, and publish a GitHub release for the tag with `Wakeboard.exe` attached. Tags carrying a prerelease suffix such as `v1.2.3-beta.1` are marked as prereleases. Downloading the executable from the releases page therefore does not require a GitHub login, unlike workflow artifacts.

## Install

Run the built executable:

```powershell
.\artifacts\win-x64\Wakeboard.exe install
```

The executable requests administrator elevation and then:

1. Confirms that the selected TCP port is available.
2. Prompts twice for a shared password of at least eight characters.
3. Copies itself into `%ProgramData%\Wakeboard\bin`.
4. Stores a PBKDF2 password hash and generated session secret with restricted permissions.
5. Installs an automatically starting `Wakeboard` Windows service under `LocalService`.
6. Adds the `Wakeboard web interface` inbound firewall rule for Private network profiles.
7. Starts the service.

The default dashboard URL is `http://localhost:3000`. To select another port:

```powershell
.\artifacts\win-x64\Wakeboard.exe install --port 8080
```

If the port is occupied, stop the process using it or choose another port. Other computers on the LAN can use `http://<wakeboard-computer-name>:<port>`. If Windows classifies the LAN as Public, change it to Private or deliberately adjust the firewall rule.

### Tailscale Serve HTTPS

Wakeboard supports HTTPS termination through Tailscale Serve while keeping the backend on local HTTP. On the Wakeboard computer, run:

```powershell
tailscale serve --bg 3000
```

Open the `https://<computer>.<tailnet>.ts.net` URL reported by Tailscale. Wakeboard accepts Tailscale's forwarded HTTPS scheme and hostname only when the proxy connection comes from the local machine; forwarded headers from LAN clients are ignored. The UI and API remain same-origin, so CORS does not need to be enabled.

### Existing data and password resets

When installation is launched from a repository checkout, `data\config.json` is imported only if `%ProgramData%\Wakeboard\data\config.json` does not already exist. Existing ProgramData host configuration is never overwritten by that import.

Running `install` again preserves the ProgramData host file but replaces the service settings, including the shared password and session secret. This is also the supported password-reset procedure. The installer removes the legacy `WakeboardWolHelper` service if it is present.

## Service commands

| Command | Behavior |
| --- | --- |
| `Wakeboard.exe status` | Displays the Windows service state, local URL, and configuration path. |
| `Wakeboard.exe update` | Replaces the installed executable and restarts the service while preserving settings and hosts. |
| `Wakeboard.exe uninstall` | Removes the service, firewall rule, and installed executable while preserving settings and hosts. |
| `Wakeboard.exe uninstall --purge` | Removes the service, firewall rule, executable, settings, and saved hosts. |

`install`, `update`, and `uninstall` request elevation automatically. Run `update` from a new external copy of `Wakeboard.exe`; the installed copy cannot replace itself. Removal is scheduled briefly after the command exits so Windows can release the running executable.

## Persistence, backup, and restore

Mutable state is kept outside the executable:

```text
%ProgramData%\Wakeboard\settings.json     password hash, session secret, port
%ProgramData%\Wakeboard\data\config.json saved hosts
%ProgramData%\Wakeboard\bin\Wakeboard.exe installed application
```

The service serializes host mutations within the process and replaces `config.json` atomically. Invalid JSON, unsupported schema versions, and invalid host records are reported rather than silently replaced. Only one Wakeboard process should use a given data directory.

To back up saved hosts consistently:

```powershell
Stop-Service Wakeboard
Copy-Item -Recurse "$env:ProgramData\Wakeboard\data" ".\wakeboard-data-backup"
Start-Service Wakeboard
```

Restore by stopping the service, replacing `%ProgramData%\Wakeboard\data`, and starting it again. Back up `settings.json` separately only if you also need to preserve the password and active session-signing secret; treat that file as sensitive.

ProgramData persistence survives page refreshes, service restarts, Windows reboots, reinstalls, and executable updates. It does not protect against accidental deletion, disk failure, or filesystem corruption.

## Authentication and HTTP security

- The installer stores a salted PBKDF2-SHA256 password hash, never the plaintext password.
- Successful login creates a signed, HTTP-only, same-site session cookie valid for 12 hours.
- Login is limited to ten failed attempts per remote address in a rolling 15-minute window.
- Every API endpoint except login and session inspection requires a valid session.
- State-changing requests require a matching same-origin `Origin` header.
- Plain HTTP does not encrypt the password or session in transit. Use Wakeboard only on a network you trust.

## Wake-on-LAN behavior

- Adapter discovery includes active, non-loopback Windows interfaces with a usable IPv4 address and subnet mask.
- Windows adapter IDs are stored with hosts. If an adapter is removed or replaced, edit the host and select the new adapter.
- Broadcast addresses are recalculated from the selected adapter's current IPv4 addresses and subnet masks for every wake request.
- Wakeboard binds UDP sockets to the selected adapter and sends three standard magic packets to UDP port 9 for each eligible IPv4 address.
- A successful response means Windows accepted the UDP transmissions. Wake-on-LAN has no acknowledgement and cannot confirm that the device booted.

For packet-level testing, capture on the selected LAN adapter with Wireshark using the display filter `udp.port == 9`. Each standard magic packet contains six `FF` bytes followed by the target MAC address repeated sixteen times.

## Reachability checks

The optional check target accepts a hostname, IPv4 address, or IPv6 address without a URL scheme or path. Wakeboard sends one ICMP echo with a 1.5-second timeout when the dashboard loads, when the dashboard is refreshed, or when **Check now** is selected.

DNS problems, host firewalls, endpoint security, sleeping network adapters, and network policy can all produce **No ping response**. Treat only a successful reply as definitive.

## Troubleshooting

- **The dashboard does not load:** run `Wakeboard.exe status`, confirm the reported service state, and check that the selected port is listening.
- **LAN clients cannot connect:** verify the Windows network profile is Private and inspect the `Wakeboard web interface` firewall rule.
- **No adapters appear:** confirm at least one non-loopback Windows adapter is active and has an IPv4 address and subnet mask.
- **Packets send but the device stays off:** verify firmware and adapter WoL settings, the target MAC address, and that the chosen adapter shares the target's broadcast domain.
- **Broadcasts are missing:** allow outbound UDP/9 from the installed executable and check for VPNs, VLAN boundaries, Wi-Fi client isolation, or network equipment that suppresses directed broadcasts.
- **A saved adapter is unavailable:** edit the device and select its current Windows adapter.
- **Configuration errors appear:** inspect `%ProgramData%\Wakeboard\data\config.json`; Wakeboard intentionally leaves malformed data untouched so it can be repaired or restored.

## Repository layout

- `ui/` - React and Vite browser interface
- `src/Wakeboard.Server/` - ASP.NET Core server, persistence, authentication, Windows networking, and service lifecycle
- `tests/Wakeboard.Tests/` - .NET unit tests
- `build/Dockerfile` - reproducible build environment and Windows cross-publish pipeline
- `scripts/build.ps1` - packaging entry point
- `data/` - optional one-time import location for an existing `config.json`; runtime data lives in ProgramData

The main browser routes cover login/logout, host CRUD, interface discovery, wake requests, and per-host status checks. Password material and Windows networking remain inside the service process.
