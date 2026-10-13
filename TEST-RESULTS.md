# v0.5 verification record (2026-10-13)

Confirmed in the artifact-producing environment:

- Source-only package file walk: no EXE/DLL or compiled binaries shipped.
- `tests/static-audit.py`: passed (read-only mapping, guest loopback management, HereSphere interface, clipboard, source location, VR button styling and network guard configuration).
- Embedded `ui/index.html` JavaScript extracted and parsed with `node --check`: passed.
- C# source tokenized using Pygments C# lexer with zero lexical-error tokens: passed (this is not compilation).
- Zip file readable and named payloads included. File-hash manifest generated after final package assembly.

**NOT TESTED:** Windows 11 PowerShell `Add-Type` compilation, guest loopback access after the modified guard, actual fetching of source-site HTML or yt-dlp metadata, individual website discovery quality, HereSphere API parsing and thumbnail display, playback/seeking and LAN isolation. These are mandatory acceptance tests in a disposable Windows Sandbox. Treat v0.5 as an alpha test build until they pass.

If compiling fails inside Windows Sandbox, report the first `Add-Type` compiler diagnostic along with the checked-out source commit. Avoid changing security/network rules blindly.
