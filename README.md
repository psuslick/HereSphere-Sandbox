# HereSphere Sandbox link server (v0.4)

This is a small Windows Sandbox link server for public video pages and media URLs. HereSphere on the Windows host sends a page URL to the Sandbox server. The server asks `yt-dlp` to resolve it, then gives HereSphere short, session-only proxy URLs for video and optional audio. The Sandbox proxy forwards byte ranges and rewrites HLS playlists. A small static HTML fallback can find common `<video>`, `<source>`, and Open Graph media tags.

The project was inspired by [renierr/heresphere-server](https://github.com/renierr/heresphere-server). This is an independent, reduced implementation focused on disposable streaming. It has no accounts, downloads, library, or persistent database.

## Layout and trust boundary

- `src/HereSphereSandboxServer.cs` is the application. The Sandbox **clones it from your public Git repository on every launch** into disposable `C:\App`, then compiles it in memory. No app source is read from the host mapping.
- `host/` is a reviewed bootstrap snapshot installed on the host in `C:\SandboxBootstrap\HereSphere`. Its mapped copy is read-only inside Sandbox. Updating the public repository does not silently update this trusted layer.
- Portable Git, `yt-dlp.exe`, and optional FFmpeg live in `C:\SandboxBootstrap` on the host and are mapped read-only. No npm, pip, or tool installation occurs at startup.
- `C:\App`, `C:\HereSphereRuntime`, the guest profile, temporary files, caches, and all resolved media state are disposable Sandbox storage. Transient signed URLs, headers, cookies, and media sessions may live in Sandbox memory **or Sandbox-local files**, but never in the repository or host mapping. This implementation keeps resolved media records in memory and directs caches to the guest runtime.

## One-time host setup

1. Create a **public** Git repository and copy this zip's contents into its root. Commit and push the reviewed source. Do not add credentials, `.env` files, cookies, or runtime output. A tag is preferable for a stable release.
2. From that checkout on the host, run `host\Install-HostBootstrap.ps1` in PowerShell. It installs the two reviewed scripts and creates `C:\SandboxBootstrap\HereSphere\repo-config.psd1` only if absent. Keep that installed config outside Git.
3. Edit the installed `repo-config.psd1`: set `RepositoryUrl` to the public HTTPS clone URL and `Ref` to your branch or release tag. Do not embed credentials or query parameters.
4. Put portable Git at `C:\SandboxBootstrap\git\cmd\git.exe` and `yt-dlp.exe` at `C:\SandboxBootstrap\yt-dlp.exe`. The bootstrap also accepts the alternate paths listed in its source. Optional FFmpeg: `C:\SandboxBootstrap\ffmpeg\bin\ffmpeg.exe`. Download and inspect these tools on the host during setup or update, not during Sandbox startup. The tools remain read-only to Sandbox.
5. Keep `HereSphereSandbox.wsb` on the host and double-click it. The mapped folder must exist first. The launch command automatically runs the installed bootstrap. Its PowerShell window stays open on error.

## Each launch

The guest creates its disposable runtime, installs the guest firewall rules, clones the configured Git ref with no submodules or credential helper, prints the commit hash, compiles the server using Windows PowerShell's built-in C# compiler, and starts it on the guest IPv4 address at port 5000. The guest desktop opens a text file showing that session's address, for example `http://172.28.64.3:5000`. Set HereSphere's **Link Server** setting to that exact address. The guest IP changes between launches.

Open the address in a host browser for the status page, then try a public page or a direct `.mp4`, `.webm`, `.m4v`, `.mov`, or `.m3u8` URL through HereSphere's Link Server button. HereSphere should receive `videoUrl` and, when applicable, `audioUrl`. The media proxy supports HTTP `Range` and `HEAD`. It does not save downloads or session state after Sandbox closes.

## Network behavior and limits

Windows Sandbox networking uses a host-created virtual switch and NAT. In this design, **HereSphere initiates** TCP connections from the host to the guest's port 5000. The guest needs to reply on that established connection. The guest also needs outbound DNS (normally to a resolver on the NAT link), DHCP renewal, HTTPS for the Git clone, and public HTTP/HTTPS for extraction and media streaming. It does **not** need to initiate a TCP connection to arbitrary services on the host or other machines on the NAT subnet.

The guest guard runs before Git or `yt-dlp`. It blocks private and special IPv4 destinations, including the directly connected NAT subnet, except the discovered gateway/DNS endpoints and DHCP broadcast. Those exceptions are limited to DNS port 53 and DHCP port 67 for guest-initiated TCP/UDP traffic; ICMP is blocked. It also blocks private and special IPv6 destinations. Inbound TCP 5000 is allowed only from the discovered NAT gateway, and the app binds only the guest IPv4 address. The design relies on Windows Firewall permitting replies on that inbound connection; verify this in the live acceptance check. The app independently rejects private/local input and redirect URLs, disables proxy use and browser cross-origin reads, and never returns upstream signed URLs to HereSphere.

| Flow | Why | Intended result |
| --- | --- | --- |
| Host NAT gateway → guest TCP 5000 | HereSphere Link Server and media requests | Allowed inbound; replies on that connection |
| Guest → configured DNS address TCP/UDP 53 | Resolve public Git and media names | Allowed |
| Guest → gateway TCP/UDP 67 | DHCP renewal where applicable | Allowed |
| Guest → broadcast UDP 67 | DHCP renewal where applicable | Allowed |
| Guest → public Git/media destinations | Clone, extract, and stream | Allowed |
| Guest → other NAT peers or private LAN addresses | No application need | Blocked |
| Guest → gateway/host services on other TCP/UDP ports | No application need | Blocked |

The supplied v0.3 guard exempted the **entire** connected NAT subnet from its private-address block. That left the gateway/host and any peer on that subnet reachable for guest-initiated connections by processes other than its special `yt-dlp` rule. This revision narrows the exception to the discovered gateway/DNS addresses and ports above. The exact addresses are printed by the guest and vary by machine and session.

These guest rules are defense in depth, not a host-enforced security boundary. A compromised guest administrator can alter its own firewall; DNS traffic to the configured resolver is necessary; public addresses that route back to host services are possible; and DNS rebinding remains a residual concern. Windows Sandbox itself and a **read-only, narrow host mapping** are the persistence and file-isolation boundary. Do not map your Git checkout, Downloads, home folder, or credentials. The actual NAT address, DNS address, firewall response behavior, and HereSphere connectivity need a live check on your machine.

HereSphere must receive the guest's session URL and opaque `/media/...` paths to request playback. Those paths contain no upstream signed URL, cookie, or header, but HereSphere may keep its own browsing history or cache on the host; this application cannot control that player behavior.

## Live acceptance check

1. Launch the `.wsb`. Confirm the guest window prints `Network guard active`, the expected public repository/ref and commit, and the session URL. Confirm no downloads or package installs occur.
2. From the host, open `http://<guest-ip>:5000/health`; expect JSON with `"success":true`. From the guest, try a TCP connection to the NAT gateway on a non-DNS port and to another private LAN address; both should fail. DNS resolution and public HTTPS should work.
3. In HereSphere, set Link Server to the session URL and use the link button on a public video page. Verify playback and seeking. Test a direct MP4 and an HLS source if available. Some sites require login, DRM, JavaScript execution, special headers, or changing extractors and will not work.
4. Close Sandbox. Reopen it and confirm the old media proxy URLs no longer work and the new session starts with no prior media records.

If host-to-guest traffic fails after the guard is installed, inspect the guest firewall log and the actual guest NAT/DNS addresses before changing rules. The guard intentionally fails closed on setup errors. The guest console prints no full signed URLs or cookies.

## Development

Edit and commit source in your local Git checkout, push to the public repo, then relaunch Sandbox to test the new commit. No build artifacts are committed. Update the host bootstrap only after reviewing the scripts and rerunning `host\Install-HostBootstrap.ps1`. Keep a stable `Ref` tag for daily use, and advance it deliberately when a version has been tested.

The source compiles with Windows PowerShell 5.1 `Add-Type` and framework assemblies already present in Sandbox. Run the optional tests in an isolated development VM or Sandbox: `tests\smoke.ps1` checks compilation and core URL filtering; `tests\http-smoke.ps1` checks local HTTP behavior; `tests\network-guard.ps1` checks rule calculations with mocked guest addresses without changing firewall settings. These do not substitute for the live Sandbox/HereSphere check above.
