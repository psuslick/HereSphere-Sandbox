$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$source = Get-Content -LiteralPath (Join-Path $repository 'src\HereSphereSandboxServer.cs') -Raw
Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies @(
    'System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll'
)

$type = [HereSphereSandbox.Server]
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$validate = $type.GetMethod('TryValidatePublicHttpUri', $flags)
foreach ($case in @(
    @{ Url='http://127.0.0.1/video.mp4'; Expected=$false },
    @{ Url='http://192.168.1.1/video.mp4'; Expected=$false },
    @{ Url='http://[::1]/video.mp4'; Expected=$false },
    @{ Url='file:///C:/Windows/win.ini'; Expected=$false },
    @{ Url='https://1.1.1.1/video.mp4'; Expected=$true }
)) {
    $arguments = [object[]]@($case.Url, $null, $null)
    $accepted = [bool]$validate.Invoke($null, $arguments)
    if ($accepted -ne $case.Expected) { throw "URL policy failed for $($case.Url)" }
}

$type.GetField('_publicBaseUrl', $flags).SetValue($null, 'http://127.0.0.1:5000')
$build = $type.GetMethod('BuildFromYtDlpInfo', $flags)
$info = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$info.Add('title', 'Example')
$info.Add('url', 'https://1.1.1.1/video.mp4?mode=test')
$invokeArgs = New-Object 'System.Object[]' 1
$invokeArgs[0] = $info.PSObject.BaseObject
$result = $build.Invoke($null, $invokeArgs)
$videoUrl = [string]$result.GetType().GetField('VideoUrl').GetValue($result)
if ($videoUrl -notmatch '^http://127\.0\.0\.1:5000/media/[0-9a-f]{32}$') {
    throw 'Resolver exposed an upstream URL or returned an invalid proxy URL.'
}
Write-Output 'Smoke checks passed: compile, URL policy, proxy URL.'
