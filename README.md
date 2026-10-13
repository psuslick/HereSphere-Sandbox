# HereSphere Sandbox v0.5 — session-only catalog and VR selection (test build)

This version preserves the **working v0.4 runtime model**: double-click a Windows Sandbox `.wsb`, use a **single read-only** host mapping `C:\SandboxBootstrap` for Git/yt-dlp/optional FFmpeg and trusted bootstrap, clone your **public** repository into writable disposable `C:\App`, and run the actual media server there. There are no app binaries, npm downloads, host services, host port forwards, or writable host mappings in this package.

**v0.5 has not yet been executed on Windows Sandbox or tested against an actual HereSphere session.** Its new UI JavaScript passes `node --check`, its C# source passed static lexical analysis, and its package passed source-only/static configuration checks. The real Windows `Add-Type` compilation, catalog API responses, HereSphere library presentation, thumbnail requests, and media playback must be tested on your Windows 11 installation.

## What's new

- **Two separate HereSphere libraries** under the `/heresphere` web API: `Individual Videos` and `Discovered Videos`. An individual URL stays in the curated library, even if it also appears in discovery.
- **Two private host-local input lists**, never in public Git: `C:\SandboxBootstrap\HereSphere\Sources\videos.txt` and `websites.txt`, one public HTTP(S) URL per line. The host folder remains read-only to Windows Sandbox.
- **Session-only management UI**, reachable **only from inside Windows Sandbox**, at `http://127.0.0.1:5001` in the guest browser. It has separate tabs, thumbnail grid, search, source-list and scanning preferences, discovery status, manual rescan, and a selected-links copy panel.
- **Oversized VR mode**, with full-card-width **190 px high** selection buttons, a simplified large layout, and no hover-only selection controls. A checkbox is also available in normal desktop mode.
- **Copy the original discovered webpage URLs**, one per line, using the selected-link textbox or `Copy URLs`. No signed `/media`/CDN links are exported. The user manually pastes from Sandbox to host via the enabled clipboard and edits the host TXT file.
- Shared metadata lookup tries yt-dlp then a public-page fallback (oEmbed, Open Graph/Twitter metadata). Website scanning tries a bounded yt-dlp flat collection and a generic public anchor scan. Thumbnail bytes are proxied and cached **in Sandbox**, not fetched by HereSphere directly from the third-party website.
- Original `/stream`, `/connection_test`, `/health`, `/media/…` and HLS/HTTP-range behavior remains from v0.4. Media resolution is requested on HereSphere playback when the client sends `needsMediaSource: true`; old clients that omit it may resolve media earlier per the HereSphere contract.

## One-time deployment / upgrade from working v0.4

**Do not run application code on the Windows host.** Review and commit the package's application files to your **public** repository (`https://github.com/psuslick/HereSphere-Sandbox.git` is the configured project in the preceding test), then push the desired `main` ref. No source files are mapped from the local working tree.

The trusted host snapshot is deliberately separate from the public repository checkout and must be deployed manually:

1. With Windows Sandbox **closed**, copy this package's `host/bootstrap.ps1` and `host/Configure-NetworkGuard.ps1` into `C:\SandboxBootstrap\HereSphere\` on the host. Or review then run `host/Install-HostBootstrap.ps1` on the host; it copies these scripts without downloading anything and creates empty source TXT files if missing.
2. **Preserve** your existing `repo-config.psd1` in that folder (do not overwrite it). Confirm it points at your public repo and `EntrySource = 'src\HereSphereSandboxServer.cs'`.
3. Create `C:\SandboxBootstrap\HereSphere\Sources\videos.txt` and `websites.txt` if they do not exist. Add actual public video URLs to `videos.txt` and public site/collection URLs to `websites.txt`. The files can be empty. They are outside Git and are never writable from the VM.
4. Continue using your existing installed portable Git at `C:\SandboxBootstrap\git\cmd\git.exe`, yt-dlp at `C:\SandboxBootstrap\yt-dlp.exe`, and optional FFmpeg at `C:\SandboxBootstrap\ffmpeg\bin\ffmpeg.exe`.
5. Double-click **this package's** `HereSphereSandbox.wsb`. It retains the **visible CMD console** workaround, enables clipboard redirection as requested, and preserves read-only mapping / Protected Client / no vGPU / no devices.

The updated guest network guard keeps the validated v0.4 restricted NAT/LAN ranges but **does not block guest IPv4 loopback**, which is needed for its own browser to access the guest-only UI. Guest loopback does not expose the host. The explicit HereSphere TCP 5000 inbound allowance remains limited to the detected virtual NAT gateway.

## Each session

The console should display its normal 5 steps (`tool store` → `network guard` → `git clone` → `compile` → `ready`), plus a management UI line. This version is still loaded directly from the public Git clone into disposable writable `C:\App` using built-in PowerShell `Add-Type` compilation. It never syncs source changes back to the host.

**Inside Sandbox:** open Edge to `http://127.0.0.1:5001` for the management web UI. All settings and selections are session-only. For selections, switch to **Discovered Videos**, optionally click **ENTER VR MODE**, hit a very large **SELECT VIDEO** button, then use **Copy URLs**. In the guest's selected-URLs textarea, Ctrl+A/Ctrl+C also works as a fallback. Paste manually into the *host* `videos.txt`, if desired. Never copy transient media proxy URLs into that file.

**On Windows host:** open the guest's printed `http://<sandbox-ip>:5000/health` and expect `{"success":true,...}`. In HereSphere, open `http://<sandbox-ip>:5000/heresphere` via its browser Web Stream flow for the two separate libraries. The HereSphere **Link Server** continues to use `http://<sandbox-ip>:5000` for the existing `/stream` workflow. Do not point HereSphere to the guest-only admin `5001` port.

Guest IP can change each Sandbox launch. Closing Windows Sandbox destroys selected links, settings, catalog metadata, thumbnail cache and media sessions. **The host remains unchanged.**

## Known limitations / verification needs

- Site discovery is deliberately **bounded** (default: 48 items and 3 scanned pages per listed website; only 20 metadata probes per website). An index card from an unfamiliar site can be **unverified or a false positive** if its anchor points to a non-video. This build does **not** yet implement headless-browser JavaScript/network interception. Some sites require it.
- No provider allowlist. Generic public-media discovery gets an opportunity even for unfamiliar sites. Limits are used to contain run time, not filter site names. Public URLs only; no login/DRM/paywall bypass.
- HereSphere catalogue protocol follows publicly available examples (`HereSphere-JSON-Version: 1`, `access`, `library` with item URLs, per-item thumbnail/media response). Real HereSphere behavior **has not been validated** with this build, especially when metadata requests omit `needsMediaSource`, or when yt-dlp gives separate audio/video tracks. The old Link Server `/stream` path remains available.
- Thumbnail image proxy supports public JPEG/PNG/WebP/GIF, max 3 MiB each. Missing thumbnails return a packaged PNG placeholder compatible with image viewers. Guest memory cache is capped at 160 thumbnails; others can still be retrieved on demand.
- Guest management is a local HTTP interface with session-only state, Host/Origin checks and no host-facing listener. Clipboard redirection is enabled intentionally to allow *user-driven* copying; this grants a bidirectional clipboard channel and should not carry secrets.
- The guest firewall remains defense-in-depth, not a substitute for hypervisor isolation. Remote DNS rebinding/routing risks are not fully eliminated by this prototype. Validate LAN-deny behavior in the running guest.
- Browser refresh discards the selected set because selection currently lives in the management browser's in-memory JavaScript (not localStorage). Normal navigation between the UI's tabs does **not** clear selections.
- All code remains plain-text source; no generated/custom executables are shipped.

## Recommended first validation

1. Start `.wsb`: confirm compile succeeds, source commit is correct, and guest UI opens on 5001. If compilation fails, copy the **first** `Add-Type` error; don't change the firewall or install npm.
2. In guest UI: add one known individual video URL and one simple public site URL (in Settings, if host TXT files are empty); click **Apply settings & rescan**, observe two separate tabs, thumbnails, selection, 190-pixel VR buttons and selected-original-URL textbox.
3. From host browser: GET `/health`, GET `/heresphere` (should return two named sections). Test one item metadata URL with `POST` body `{"needsMediaSource":false}` before requesting playback.
4. In actual HereSphere: load `/heresphere`, check both sections, thumbnails, projection when inferable and playback/seek. Collect the request type/status if it does not match.
5. During the session, test outgoing DNS/public HTTP and verify guest-to-NAT and guest-to-LAN non-DNS traffic is still denied. Close Sandbox and confirm no source/settings were written to host.

## Security and source-control policy

Git is public and carries **only application/installer/source code**. Do not commit local source lists, URLs that contain personal secrets, browser profiles, or any runtime cache. The host read-only folder is the only mapping; application checkout and runtime state are entirely guest-local. All extraction and third-party binaries execute within Sandbox, not on Windows host. The host does not run an application server.
