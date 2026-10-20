# MDRemoteNG

This fork adds a startup PIN and a per-user background discovery helper to mRemoteNG. The upstream GPL licence and credits remain in place.

The upstream automatic updater is disabled because its packages would replace MDRemoteNG with a build lacking these features. Obtain updates from this fork.

## Use

1. Launch `MDRemoteNG.exe` from the build or published application folder. On the first launch choose and confirm a PIN. Every subsequent launch requires it, including launches that forward to an existing instance. Cancelling closes that launch before connection data is loaded.
2. PINs contain 4–8 printable ASCII characters: letters, numbers and punctuation, without spaces. Letters ignore case (`Ab1!` matches `aB1!`). Use eight characters for better resistance to guessing.
3. Open **Local peers → PIN & local discovery settings** to change the PIN, name, discovery or automatic connection settings.
4. To let matching peers connect to this computer, click **Auto-configure this computer** and approve the Windows administrator prompt. It enables Remote Desktop with Network Level Authentication, creates a dedicated non-administrator local account with a generated password, and fills and immediately saves all connection fields. Alternatively, click **Configure for my Windows account** and enter the password for the displayed Windows account. That button validates the password, grants RDP access to that account and saves the details without changing its password or permissions beyond RDP group membership. A Windows Hello PIN cannot substitute for the account password. Both buttons enable discovery and sharing, and add local-subnet firewall rules for Private/Domain networks. This requires Windows Pro, Enterprise, Education or a member server; Windows Home and domain controllers are unsupported. RDP opens the selected account's desktop and may disconnect an active console session. You can also enter an existing RDP, SSH or VNC account manually and enable its server separately; VNC permits a blank username. Saved connection-tree credentials are not enrolled automatically.
5. Run another MDRemoteNG install on the same private network with the same PIN. Within about 15 seconds the peers authenticate and exchange their enrolled connection settings. Each unlocked application opens the remote endpoint automatically once per discovery period. Use its peer menu entry to reconnect manually. Peers without an enrolled endpoint remain visible.

The helper starts with MDRemoteNG and registers itself in the current user's Windows logon `Run` key while discovery is enabled. It keeps discovering and serving the enrolled local endpoint after the main window closes. It runs under the signed-in Windows account; it does not require an administrator service account. It starts again at that user's next logon, rather than before anyone signs in. Desktop sessions only open while MDRemoteNG is running and unlocked. Disable local discovery in settings to remove logon startup and stop network activity; the idle helper observes the change within five seconds. PIN/account changes invalidate pending credential sharing and cached peer results.

Move or delete the application only after disabling discovery, or run `Discovery/Configure-Discovery.ps1 -Action Uninstall` to remove its logon registration. Moving the application and starting it again updates the registration.

Auto-configuration uses an account named `mdrng_` followed by part of the installation identity. Running it again rotates that account's password and saves the replacement immediately. It refuses to reuse an unrelated account or an account with administrator membership. Windows cannot reveal the password of your existing account. Disabling sharing stops publishing credentials; it does not delete the managed Windows account, disable RDP, or remove its firewall rules. If Windows setup fails partway through, some service or firewall changes may already have applied; account provisioning failures remove a newly created account where possible. An administrator can remove the managed account and rules when retiring the host.

The current-account option shares access with that account's existing Windows permissions. When its password changes, run that option again to refresh the saved credentials. It validates the supplied password before requesting elevation and revalidates it inside setup, binding the authenticated account SID to the original application user's token even when a different administrator approves UAC. Organisation policy, account restrictions or passwordless sign-in can prevent password-based RDP access.

## Network requirements

Discovery uses IPv4 subnet broadcasts on UDP 48765 and a dynamically assigned TCP port for authentication. Both installs must be on the same broadcast subnet, on Windows **Private** or **Domain authenticated** network profiles. Public networks, IPv6-only networks, routed discovery and NAT traversal are excluded. Windows Firewall or wireless client isolation can block discovery.

If inbound rules are needed, run the shipped script in an elevated PowerShell:

```powershell
.\Discovery\Configure-Discovery.ps1 -Action Firewall
```

Its rules allow only the helper executable, local-subnet sources, and Private/Domain profiles. They do not enable RDP/SSH/VNC servers or change Windows login rights. A remote server's normal certificate/host-key prompts and authentication rules still apply.

The Auto-configure button creates these helper rules together with RDP TCP/UDP rules scoped to Remote Desktop Services and its actual listener port. It respects Windows organisation policies and does not change a network's Public/Private classification. Existing firewall rules remain subject to Windows policy.

## Security model

PINs and enrolled credentials are protected at rest by Windows DPAPI for the current account in `%LOCALAPPDATA%\MDRemoteNG\profile.dat`. Startup PIN verification uses a random salt, PBKDF2-SHA256 with 600,000 iterations and a persistent escalating lockout. This application lock does not protect against code already running as the same Windows account; keep the upstream encrypted connection store configured as appropriate.

Host setup runs as a separate process only after Windows elevation. It does not read the parent's protected PIN/profile. Credentials pass through an access-controlled local named pipe, with both process identities checked, separate ephemeral RSA recipient keys and request/purpose-bound AES-GCM encryption. Passwords are never command-line arguments or temporary files. The managed account receives membership in Users and Remote Desktop Users, with a randomly generated 40-character password. The current-account path validates with a Windows network logon and grants Remote Desktop Users membership without resetting the password, changing account flags or creating another account.

LAN beacons contain only the protocol version, random install identity and negotiation port. Bouncy Castle SRP-6a with the RFC 5054 3072-bit group performs mutual PIN authentication. Each session has fresh salt and ephemeral keys. Only after mutual proof verification does AES-GCM with separate directional HKDF keys protect connection details. Recorded traffic does not expose a reusable PIN hash. The connection hostname comes from the authenticated socket's local-network source address, preventing advertised redirects to arbitrary hosts.

Anyone on the allowed subnet who knows the PIN may receive the account deliberately enrolled for this machine, even while the desktop window is closed. Enrol a dedicated account with suitable permissions. No matching PIN grants operating-system access without an enrolled account and running remote server. Peer credentials stay in memory and are not inserted into the saved connection tree. Same-user named pipes carry helper results to the desktop; profiles and discovery results have bounded sizes. Authentication has timeouts, four concurrent slots, per-address exponential backoff and a persisted global attempt budget. Peers expire after 90 seconds without renewed authentication.

## Build and verify

Use Windows with Visual Studio 2026's .NET desktop development tools and .NET 10. The RDP COM reference requires Visual Studio MSBuild, rather than `dotnet build` for the main application. `Tools/Build-MDRemoteNG.ps1` locates that MSBuild and builds the x64 application including its helper:

```powershell
.\Tools\Build-MDRemoteNG.ps1
```

The runnable folder is `mRemoteNG\bin\x64\Debug\`; keep the full folder, including `Discovery`, `Assemblies`, `Languages` and other shipped resources. `MDRemoteNG.exe` is a launcher copy of the upstream apphost; the underlying `mRemoteNG` assembly name is retained for installer/plugin compatibility.

To publish the existing self-contained configuration:

```powershell
.\Tools\Build-MDRemoteNG.ps1 -Configuration 'Release Self-Contained'
```

It publishes the helper with the same runtime deployment model. ARM64 is supported by the build script's `-Platform arm64` option and requires compatible Visual Studio desktop/COM tooling.

Focused regression checks run without the main application's COM dependency:

```powershell
dotnet run --project MDRemoteNG.Core.Tests -c Release
dotnet run --project MDRemoteNG.Discovery.Tests -c Release
dotnet run --project MDRemoteNG.Host.Tests -c Release
dotnet run --project MDRemoteNG.UI.Tests -c Release
dotnet run --project MDRemoteNG.Defaults.Tests -c Release
```

After building the self-contained application, these checks exercise its actual panel and connection-file code in memory without changing your connection files:

```powershell
dotnet run --project MDRemoteNG.App.Tests -c Release
dotnet run --project MDRemoteNG.Connection.Tests -c Release
```

The tests use temporary workspace profiles and test credentials, never your live profile. They cover PIN policy, DPAPI corruption, lockout persistence, atomic PIN/settings changes, matching/mismatching peers, encrypted traffic, message tampering/replay, network scope, rate limits, framing and PIN/settings UI behavior. Host tests use a fake Windows platform to check permissions, account ownership, policy restrictions, scoped rules, password rotation and cleanup; transport tests check encrypted replies, recipient/request binding, tampering and bounded framing. UI checks inject a fake setup result and verify immediate credential persistence, cancellation and failures. They never enable RDP or create a real account. Two-machine firewall, discovery and actual RDP/SSH/VNC sessions should also be verified in the intended network environment.

On 20 October 2026, the x64 desktop build and full self-contained publish succeeded, and all 176 focused checks passed. The startup checks create and dispose a native PIN-window handle before creating the next window, covering the post-PIN initialization failure. They also check separate instance locks, native requestor-account verification, malformed setup argument rejection, panel registration/reuse, legacy default settings, and production XML round trips of current 2.9 and legacy 2.8 formats with encrypted credentials. Required desktop/helper/runtime/language files were verified. The MSI compiled and its payload and separate product identity were checked through read-only MSI inspection with ICE validation suppressed locally; full Windows Installer Service validation remains to be run on a packaging machine. The broader upstream test project has existing compilation errors and is not included in the passing count. Elevated live host configuration, alternate-administrator UAC, ARM64 and two-machine remote sessions have not been exercised here.

Startup failures now display an error without constructing the main window before unlock. Details are recorded in `%LOCALAPPDATA%\MDRemoteNG\startup-error.log`.
