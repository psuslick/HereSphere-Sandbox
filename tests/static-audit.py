"""Zero-dependency package audit. Does not replace Windows Sandbox integration tests."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
wsb=(root/'HereSphereSandbox.wsb').read_text()
source=(root/'src'/'HereSphereSandboxServer.cs').read_text()
bootstrap=(root/'host'/'bootstrap.ps1').read_text()
firewall=(root/'host'/'Configure-NetworkGuard.ps1').read_text()
ui=(root/'ui'/'index.html').read_text()
assert '<ReadOnly>true</ReadOnly>' in wsb
assert '<ClipboardRedirection>Enable</ClipboardRedirection>' in wsb
assert '<ProtectedClient>Enable</ProtectedClient>' in wsb
assert '<HostFolder>C:\\SandboxBootstrap</HostFolder>' in wsb
assert 'clone' in bootstrap and 'C:\\App' in bootstrap
assert 'C:\\SandboxBootstrap\\HereSphere\\Sources' in source
assert 'IPAddress.Loopback, 5001' in source
assert 'new TcpListener(listenAddress, port)' in source
assert '/heresphere/item/' in source and '/api/catalog' in source
assert 'HereSphere-JSON-Version' in source
assert 'needsMediaSource' in source
assert 'height:190px' in ui and 'SELECT VIDEO' in ui
assert "@('127.0.0.0','127.255.255.255')" not in firewall
assert 'C:\\SandboxBootstrap' in firewall or 'RemoteAddress' in firewall
assert 'Host' in source and 'Origin' in source
assert not list(root.rglob('*.exe')) and not list(root.rglob('*.dll'))
print('Source-only/static configuration audit passed. Windows C# compile and HereSphere integration NOT tested.')
