param([string]$SourcePath, [int]$Port)
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath $SourcePath -Raw
Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies @(
    'System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll'
)
[HereSphereSandbox.Server]::Run($Port, '', "http://127.0.0.1:$Port")
