# Wakeboard

Wakeboard is a native Windows Wake-on-LAN dashboard packaged as one self-contained `Wakeboard.exe`. The executable contains the web UI, HTTP server, authentication, host storage, Windows adapter discovery, ICMP status checks, and UDP magic-packet sender.

Saved devices can include a hostname or IPv4 address for a liveliness check. **Awake** means that target replied to ICMP. **No ping response** is deliberately inconclusive: a running computer can block ping through its firewall or network policy.

## Requirements

- 64-bit Windows 10/11 or Windows Server
- Administrator access for installation, firewall configuration, update, and removal
- Wake-on-LAN enabled in each target computer's firmware, operating system, and network adapter
- Target computers on a network where IPv4 directed broadcasts are permitted
- A trusted private LAN; Wakeboard is not designed for direct internet exposure

## Build

The build uses Docker only as a reproducible compiler. The resulting application has no Docker dependency.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1
```

The output is `artifacts\win-x64\Wakeboard.exe`. The build compiles the React UI, runs the .NET test suite, and cross-publishes a self-contained `win-x64` single-file application.

## Install

Run the built executable from this repository so the installer can migrate the existing `data\config.json` automatically:

```powershell
.\artifacts\win-x64\Wakeboard.exe install
```

Windows will request administrator approval. The installer prompts for the shared dashboard password, installs an auto-starting Windows service under the low-privilege `LocalService` account, and permits the web port on Private Windows Firewall profiles. The default URL is:

```text
http://localhost:3001
```

To choose another port:

```powershell
.\artifacts\win-x64\Wakeboard.exe install --port 8080
```

If the previous Docker application still owns the chosen port, stop/remove that old container first or select another port. The native installer removes the old `WakeboardWolHelper` Windows service after confirming that its web port is available.

Other computers on the LAN can browse to `http://<wakeboard-computer-name>:<port>`. If Windows classifies the LAN as Public, either change the network profile to Private or deliberately adjust the `Wakeboard web interface` firewall rule.

## Service commands

Run these using any newly built/downloaded copy of `Wakeboard.exe`:

```powershell
# Show service state, local URL, and data path
.\Wakeboard.exe status

# Replace the installed program while preserving settings and hosts
.\Wakeboard.exe update

# Remove the service and program but preserve settings/hosts
.\Wakeboard.exe uninstall

# Remove the service, program, settings, and all saved hosts
.\Wakeboard.exe uninstall --purge
```

`install`, `update`, and `uninstall` request elevation automatically. For an update, do not run the copy already installed under `%ProgramData%`; run the new external executable.

## Data and backups

The single executable does not embed mutable state. Windows stores it separately so replacing the executable cannot erase it:

```text
%ProgramData%\Wakeboard\settings.json    password hash, session secret, port
%ProgramData%\Wakeboard\data\config.json saved hosts
%ProgramData%\Wakeboard\bin\Wakeboard.exe installed program
```

Only the PBKDF2 password hash is stored. Host updates are serialized and `config.json` is replaced atomically. Invalid or unsupported configuration is reported rather than silently overwritten. One Wakeboard service should use a data directory at a time.

To make a consistent backup, stop the service briefly and copy its data folder:

```powershell
Stop-Service Wakeboard
Copy-Item -Recurse "$env:ProgramData\Wakeboard\data" ".\wakeboard-data-backup"
Start-Service Wakeboard
```

Restore by stopping the service, replacing `%ProgramData%\Wakeboard\data`, and starting it again. Local persistence survives browser refreshes, service restarts, Windows reboots, and executable updates, but it cannot protect against deletion, disk failure, or filesystem corruption; keep an external backup.

## Network behavior and troubleshooting

- The adapter list comes directly from active, non-loopback Windows IPv4 interfaces.
- Wakeboard recalculates directed broadcast addresses from the selected adapter's current address and subnet mask on every request.
- It binds UDP to that adapter and sends three standard magic packets to UDP port 9 per eligible IPv4 address.
- A successful result means Windows accepted the transmissions; Wake-on-LAN has no acknowledgement and does not prove the target booted.
- Windows Firewall or endpoint security must permit outbound UDP/9 and ICMP from the installed executable.
- VPNs, Wi-Fi client isolation, VLAN boundaries, and routers that suppress broadcasts can block WoL. Select an adapter on the target computer's broadcast domain.
- A target that does not answer ping may still be awake. Test its ICMP firewall policy before treating the status as authoritative.
- The HTTP login is intended for a trusted LAN. The cookie is HTTP-only and same-site, and mutations require a same-origin request, but plain HTTP does not encrypt the password in transit.

For packet-level testing, capture traffic on the target LAN with Wireshark using `udp.port == 9`; a wake request should show three broadcasts containing six `FF` bytes followed by the target MAC repeated sixteen times.

## Development layout

- `ui/` — React/Vite browser interface
- `src/Wakeboard.Server/` — embedded ASP.NET Core server, persistence, auth, networking, and service lifecycle
- `tests/Wakeboard.Tests/` — unit tests
- `build/Dockerfile` — build-only Linux container that cross-publishes the Windows executable
- `scripts/build.ps1` — reproducible packaging entry point

The browser API is session-protected: host CRUD, Windows interface discovery, wake requests, and per-host status checks. Password material and Windows networking remain entirely inside the native process.
