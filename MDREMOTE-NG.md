# MDRemoteNG

This fork adds a startup PIN and a per-user background discovery helper to mRemoteNG. The upstream GPL licence and credits remain in place.

The upstream automatic updater is disabled because its packages would replace MDRemoteNG with a build lacking these features. Obtain updates from this fork.

## Use

1. Launch `MDRemoteNG.exe` from the build or published application folder. On the first launch choose and confirm a PIN. Every subsequent launch requires it, including launches that forward to an existing instance. Cancelling closes that launch before connection data is loaded.
2. PINs contain 4–8 printable ASCII characters: letters, numbers and punctuation, without spaces. Letters ignore case (`Ab1!` matches `aB1!`). Use eight characters for better resistance to guessing.
3. Open **Local peers → PIN & local discovery settings** to change the PIN, name, discovery or automatic connection settings.
4. Select **RDP**, **SSH** or **VNC**, then click **Auto-configure this computer** (or **Configure VNC desktop sharing**) and approve Windows elevation. RDP and SSH also offer **Configure for my Windows account**, which prompts for that account's password without changing it. Each protocol is saved immediately and enables discovery. Configure more than one protocol to keep them available together. Manual server details are still supported.
5. Run another MDRemoteNG install on the same private network with the same PIN. Within about 15 seconds the peers authenticate and exchange their enrolled connection settings. Each unlocked application opens each shared protocol automatically once per discovery period. Use the protocol entries under that peer to reconnect manually. Peers without an enrolled endpoint remain visible.

The helper starts with MDRemoteNG and registers itself in the current user's Windows logon `Run` key while discovery is enabled. It keeps discovering and serving the enrolled local endpoint after the main window closes. It runs under the signed-in Windows account; it does not require an administrator service account. It starts again at that user's next logon, rather than before anyone signs in. Desktop sessions only open while MDRemoteNG is running and unlocked. Disable local discovery in settings to remove logon startup and stop network activity; the idle helper observes the change within five seconds. PIN/account changes invalidate pending credential sharing and cached peer results.

Move or delete the application only after disabling discovery, or run `Discovery/Configure-Discovery.ps1 -Action Uninstall` to remove its logon registration. Moving the application and starting it again updates the registration.

Dedicated account setup uses `mdrng_` for RDP and `mdssh_` for SSH, followed by part of the installation identity. Separate accounts prevent one protocol's password rotation from breaking another. Running it again rotates that account's password and saves the replacement immediately. Standard-account setup refuses to reuse an unrelated account or an account with administrator membership. Windows cannot reveal the password of your existing account. Disabling sharing stops publishing credentials; it does not delete the managed Windows account, disable RDP, or remove its firewall rules. If Windows setup fails partway through, some service or firewall changes may already have applied; account provisioning failures remove a newly created account where possible. An administrator can remove the managed account and rules when retiring the host.

SSH also offers an unchecked **Use a dedicated administrator account for SSH** option. It creates a separate `mdadm_` account with an administrator-specific ownership marker and requires Windows elevation. Standard SSH remains the default; the choice never promotes the existing `mdssh_` account or your current Windows account. Matching-PIN peers on the permitted subnet receive this administrator account's credentials when shared. Its disabled account receives group membership and an effective SSH-policy check before activation; existing owned administrator accounts rotate their password on reconfiguration. Switching back to standard SSH or unsharing stops advertising the administrator endpoint but does not remove that account or revoke credentials already received. Remove the Windows account or change its password to revoke that access.

The current-account option shares access with that account's existing Windows permissions. When its password changes, run that option again to refresh the saved credentials. It validates the supplied password before requesting elevation and revalidates it inside setup, binding the authenticated account SID to the original application user's token even when a different administrator approves UAC. Organisation policy, account restrictions or passwordless sign-in can prevent password-based RDP access.

## SSH and encrypted VNC

| Protocol | Access | Host setup |
| --- | --- | --- |
| RDP | A Windows account's desktop session | RDP with NLA; Pro, Enterprise, Education or member server required. |
| SSH | A terminal | Windows OpenSSH optional feature on Windows 10 build 1809+ or member server, including Home editions. Existing port and host keys are preserved. Initial installation may need Windows Update/WSUS and several minutes. |
| VNC | The existing primary desktop | Small managed server inside the per-user helper, pinned TLS and a generated 256-bit token. No separate VNC installation or Windows account is required. |

Domain controllers are excluded from automatic setup. SSH supports dedicated or current-account credentials; Microsoft Entra accounts need the dedicated option. Dedicated RDP/SSH accounts are independent. Existing SSH settings are preserved: incompatible/conditional password policies, custom service paths, stale live configuration, broad firewall allows, disabled firewalls or default-Allow inbound policies cause a safe failure. A temporary block guard protects a fresh OpenSSH installation until scoped Private/Domain local-subnet rules are applied. Host-key pins derive from the actual private host keys and pass through PIN-authenticated discovery to PuTTY.

VNC runs in the signed-in user's session and shares its primary screen at up to eight changed-region updates per second. Only an authenticated client triggers capture; one desktop session and four pending TLS handshakes are permitted. Windows sign-in, UAC and locked desktops are excluded. A lock/display failure closes the session; reconnect after unlocking. Clipboard and file transfer are not enabled for this embedded server. VNC uses TCP 5900 when available or another port shown in the form. Its RFB traffic passes through pinned TLS; auto-configured VNC has no plaintext fallback. The private certificate stays in the protected profile; Schannel uses a temporary user key during hosting, removed on certificate disposal. Existing manual external VNC entries retain their original authentication/transport behavior.

The sharing checkbox applies to the selected protocol. Uncheck it and Save to remove only that endpoint; **Stop sharing all connections** removes all endpoints when saved. Removing VNC or disabling discovery stops its listener within five seconds. Removing RDP/SSH sharing stops publishing credentials but leaves Windows accounts, services and firewall rules in place. Credentials already received remain valid until their account password or VNC configuration is regenerated.

The embedded server uses [RemoteViewing 1.1.0](https://github.com/SeekHisKingdom/RemoteViewing), with its BSD two-clause license shipped at `Licenses/RemoteViewing-LICENSE.txt`. SSH uses Microsoft's [installation](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse) and [configuration](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh-server-configuration) interfaces.

The viewer builds from the official [VncSharpCore cursor fix](https://github.com/mRemoteNG/VncSharpCore/commit/dc87ba02b0090331d1956cd4a6ba0168ecd301d6), pinned under `External/VncSharpCore`. NuGet 1.2.1 omitted the cursor resource and threw `Value cannot be null (stream)` after authentication. The source build restores that resource and retains the original assembly identity. Local changes add bounded bulk pixel reads and continuous Zlib compression decoding. Its GPL, compression license and provenance ship under `Licenses/`; the source and modification notes remain in the repository.

## Network requirements

Discovery uses IPv4 subnet broadcasts on UDP 48765 and a dynamically assigned TCP port for authentication. Both installs must be on the same broadcast subnet, on Windows **Private** or **Domain authenticated** network profiles. Public networks, IPv6-only networks, routed discovery and NAT traversal are excluded. Windows Firewall or wireless client isolation can block discovery.

If inbound rules are needed, run the shipped script in an elevated PowerShell:

```powershell
.\Discovery\Configure-Discovery.ps1 -Action Firewall
```

Its rules allow only the helper executable, local-subnet sources, and Private/Domain profiles. They do not enable RDP/SSH/VNC servers or change Windows login rights. A remote server's normal certificate/host-key prompts and authentication rules still apply.

For RDP, the Auto-configure button creates these helper rules together with RDP TCP/UDP rules scoped to Remote Desktop Services and its actual listener port. It respects Windows organisation policies and does not change a network's Public/Private classification. Existing firewall rules remain subject to Windows policy.

## Security model

PINs and enrolled credentials are protected at rest by Windows DPAPI for the current account in `%LOCALAPPDATA%\MDRemoteNG\profile.dat`. Startup PIN verification uses a random salt, PBKDF2-SHA256 with 600,000 iterations and a persistent escalating lockout. This application lock does not protect against code already running as the same Windows account; keep the upstream encrypted connection store configured as appropriate.

Host setup runs as a separate process only after Windows elevation. It does not read the parent's protected PIN/profile. Credentials pass through an access-controlled local named pipe, with both process identities checked, separate ephemeral RSA recipient keys and request/purpose-bound AES-GCM encryption. Passwords are never command-line arguments or temporary files. Managed RDP accounts receive Users and Remote Desktop Users membership; standard managed SSH accounts receive Users membership; the explicitly selected SSH administrator account also receives Administrators membership. Both use randomly generated 40-character passwords. The current-account path validates with a Windows network logon; RDP setup grants Remote Desktop Users membership. Neither current-account option resets the password, changes account flags or creates another account.

LAN beacons contain only the protocol version, random install identity and negotiation port. Bouncy Castle SRP-6a with the RFC 5054 3072-bit group performs mutual PIN authentication. Each session has fresh salt and ephemeral keys. Only after mutual proof verification does AES-GCM with separate directional HKDF keys protect connection details. Recorded traffic does not expose a reusable PIN hash. The connection hostname comes from the authenticated socket's local-network source address, preventing advertised redirects to arbitrary hosts.

Anyone on the allowed subnet who knows the PIN may receive credentials for endpoints deliberately shared by this machine, even while the desktop window is closed. For RDP and SSH, enrol a dedicated account with suitable permissions. A matching PIN enables automatic access only to a deliberately shared endpoint with a running listener. Peer credentials stay in memory and are not inserted into the saved connection tree. Same-user named pipes carry helper results to the desktop; profiles and discovery results have bounded sizes. Authentication has timeouts, four concurrent slots, per-address exponential backoff and a persisted global attempt budget. Peers expire after 90 seconds without renewed authentication.

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
dotnet run --project MDRemoteNG.Desktop.Tests -c Release
dotnet run --project MDRemoteNG.Vnc.Tests -c Release
dotnet run --project MDRemoteNG.UI.Tests -c Release
dotnet run --project MDRemoteNG.Defaults.Tests -c Release
```

After building the self-contained application, these checks exercise its actual panel and connection-file code in memory without changing your connection files:

```powershell
dotnet run --project MDRemoteNG.App.Tests -c Release
dotnet run --project MDRemoteNG.Connection.Tests -c Release
```

The tests use temporary workspace profiles and test credentials, never your live profile. They cover PIN policy, DPAPI corruption, lockout persistence, atomic PIN/settings changes, matching/mismatching peers, encrypted traffic, message tampering/replay, network scope, rate limits, framing and PIN/settings UI behavior. Host tests use a fake Windows platform to check permissions, account ownership, policy restrictions, scoped rules, password rotation and cleanup; transport tests check encrypted replies, recipient/request binding, tampering and bounded framing. UI checks inject a fake setup result and verify immediate credential persistence, cancellation and failures. They never enable host services or create real accounts. Administrator SSH checks cover separate account ownership, disabled enrollment, role-aware policy evaluation, password rotation, failure cleanup and encrypted request/result access levels. Two-machine firewall, discovery and actual RDP/SSH/VNC sessions should also be verified in the intended network environment.

The x64 desktop build and full self-contained publish succeeded, and all 339 focused checks passed. The startup checks create and dispose a native PIN-window handle before creating the next window, covering the post-PIN initialization failure. They also check separate instance locks, native requestor-account verification, malformed setup argument rejection, panel registration/reuse, legacy default settings, and production XML round trips of current 2.9 and legacy 2.8 formats with encrypted credentials. Required desktop/helper/runtime/language files were verified. The MSI compiled and its payload and separate product identity were checked through read-only MSI inspection with ICE validation suppressed locally; full Windows Installer Service validation remains to be run on a packaging machine. The broader upstream test project has existing compilation errors and is not included in the passing count. Synthetic VNC checks exercise real loopback TLS, invalid pins/tokens, RFB authentication, synthetic pixels, fake input callbacks, session limits and display-loss recovery. Elevated live setup, alternate-administrator UAC, real screen capture/input, ARM64 and two-machine remote sessions have not been exercised here.

Startup failures now display an error without constructing the main window before unlock. Details are recorded in `%LOCALAPPDATA%\MDRemoteNG\startup-error.log`.

The viewer correction additionally passes 17 checks using the actual Windows Forms control over the pinned TLS tunnel. They verify the embedded cursor, password authentication, all decoded synthetic pixels, repeated reconnects with the same control, capture-loss cleanup and rejection of incorrect certificate pins, sharing tokens and RFB passwords. These checks use hidden, view-only controls and a synthetic desktop; they do not show windows, capture a real screen, access the clipboard or send Windows input.

VNC performance changes coalesce dirty tiles into at most 64 rectangles, batch
framebuffer output into 64 KiB writes, disable TCP delay and negotiate Zlib
compression. Small changes retain partial updates; large changes use a combined
region. The eight-updates-per-second capture limit remains. Update both the
viewer and the sharing computer's helper for the full improvement; the new
viewer can also negotiate compression with the earlier embedded helper.

Synthetic full-frame readiness measurements, excluding synthetic image
generation, improved from 991 ms to 110 ms at 1920x1080 and from 1,797 ms to
117 ms at 2560x1440 with both ends updated. Measured RFB payloads fell about
90%. With only the viewer updated, the corresponding results were 439 ms and
644 ms. These loopback measurements are not a real-network frame-rate guarantee.
The performance changes pass 60 actual-viewer/decoder checks and 50 synthetic
desktop/TLS checks, including cropped requests, cache changes, bounded output,
partial reads and malformed continuous compressed streams. Tests use synthetic
pixels and do not capture a real desktop or send Windows input.
