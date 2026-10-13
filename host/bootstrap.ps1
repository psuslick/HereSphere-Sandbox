$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$shareRoot     = 'C:\SandboxBootstrap'
$bootstrapRoot = Join-Path $shareRoot 'HereSphere'
$runtime       = 'C:\HereSphereRuntime'
$appRoot       = 'C:\App'
$temp          = Join-Path $runtime 'Temp'
$sandboxHome   = Join-Path $runtime 'Home'
$cache         = Join-Path $runtime 'Cache'
$configPath    = Join-Path $bootstrapRoot 'repo-config.psd1'
$port          = 5000

Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ' HereSphere Sandbox Server - Git checkout bootstrap' -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host 'Tools/security bootstrap: read-only C:\SandboxBootstrap'
Write-Host 'Application source: cloned fresh into disposable C:\App'
Write-Host 'No package installation or dependency download is performed.'
Write-Host ''

if (-not (Test-Path -LiteralPath $bootstrapRoot -PathType Container)) {
    throw "HereSphere bootstrap folder not found at $bootstrapRoot."
}
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Repository configuration not found: $configPath"
}

$config = Import-PowerShellDataFile -LiteralPath $configPath
$repositoryUrl = [string]$config.RepositoryUrl
$repositoryRef = [string]$config.Ref
$entrySource   = [string]$config.EntrySource
$allowSubmodules = [bool]$config.AllowSubmodules

if ([string]::IsNullOrWhiteSpace($repositoryUrl) -or $repositoryUrl -match 'REPLACE_ME') {
    throw @"
RepositoryUrl has not been configured.
Edit on the HOST:
  C:\SandboxBootstrap\HereSphere\repo-config.psd1
and set RepositoryUrl to the HTTPS Git repository you control.
"@
}

$repoUri = $null
if (-not [Uri]::TryCreate($repositoryUrl, [UriKind]::Absolute, [ref]$repoUri)) {
    throw "RepositoryUrl is not a valid absolute URL: $repositoryUrl"
}
if ($repoUri.Scheme -ne 'https') {
    throw 'RepositoryUrl must use HTTPS. SSH/file/local Git transports are deliberately disabled.'
}
if ($repoUri.UserInfo -or $repoUri.Query -or $repoUri.Fragment) {
    throw 'RepositoryUrl must not contain credentials, query parameters, or a fragment.'
}
if ([string]::IsNullOrWhiteSpace($repositoryRef)) { throw 'Ref must not be empty.' }
if ([string]::IsNullOrWhiteSpace($entrySource)) { throw 'EntrySource must not be empty.' }

# Everything writable belongs to the disposable Sandbox filesystem.
Remove-Item -LiteralPath $runtime -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $appRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $runtime,$temp,$sandboxHome,$cache | Out-Null
$env:TEMP = $temp
$env:TMP = $temp
$env:XDG_CACHE_HOME = $cache
$env:GIT_LFS_SKIP_SMUDGE = '1'
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$env:GIT_CONFIG_NOSYSTEM = '1'
$env:GIT_CONFIG_GLOBAL = 'NUL'

function Find-FirstFile {
    param([string[]]$Candidates)
    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

$git = Find-FirstFile @(
    (Join-Path $shareRoot 'git\cmd\git.exe'),
    (Join-Path $shareRoot 'git\bin\git.exe'),
    (Join-Path $shareRoot 'tools\git\cmd\git.exe')
)
if (-not $git) {
    throw @"
Portable Git was not found in the read-only SandboxBootstrap mapping.
Expected one of:
  C:\SandboxBootstrap\git\cmd\git.exe
  C:\SandboxBootstrap\git\bin\git.exe
  C:\SandboxBootstrap\tools\git\cmd\git.exe
"@
}

$gitRoot = Split-Path -Parent (Split-Path -Parent $git)
$env:GIT_EXEC_PATH = Join-Path $gitRoot 'mingw64\libexec\git-core'
$env:PATH = "$(Join-Path $gitRoot 'cmd');$(Join-Path $gitRoot 'usr\bin');$(Join-Path $gitRoot 'mingw64\bin');$env:PATH"

$ytDlp = Find-FirstFile @(
    (Join-Path $shareRoot 'yt-dlp.exe'),
    (Join-Path $shareRoot 'tools\yt-dlp.exe'),
    (Join-Path $shareRoot 'yt-dlp\yt-dlp.exe'),
    (Join-Path $bootstrapRoot 'Tools\yt-dlp.exe')
)
if (-not $ytDlp) {
    throw @"
yt-dlp.exe was not found in the read-only SandboxBootstrap mapping.
Place it on the HOST at one of:
  C:\SandboxBootstrap\yt-dlp.exe
  C:\SandboxBootstrap\tools\yt-dlp.exe
  C:\SandboxBootstrap\yt-dlp\yt-dlp.exe
No tool is downloaded automatically.
"@
}

$ffmpeg = Find-FirstFile @(
    (Join-Path $shareRoot 'ffmpeg.exe'),
    (Join-Path $shareRoot 'tools\ffmpeg.exe'),
    (Join-Path $shareRoot 'ffmpeg\bin\ffmpeg.exe'),
    (Join-Path $bootstrapRoot 'Tools\ffmpeg\bin\ffmpeg.exe')
)
if ($ffmpeg) {
    $env:PATH = "$(Split-Path -Parent $ffmpeg);$env:PATH"
}

Write-Host '[1/5] Read-only tool store' -ForegroundColor Cyan
Write-Host ('      git:    {0}' -f $git)
Write-Host ('      yt-dlp: {0}' -f $ytDlp)
Write-Host ('      yt-dlp SHA256: {0}' -f (Get-FileHash -LiteralPath $ytDlp -Algorithm SHA256).Hash)
if ($ffmpeg) {
    Write-Host ('      ffmpeg: {0}' -f $ffmpeg)
} else {
    Write-Host '      ffmpeg: not found (optional for current resolver)' -ForegroundColor DarkYellow
}

Write-Host '[2/5] Applying guest network guard before any Git/network activity...' -ForegroundColor Cyan
& (Join-Path $bootstrapRoot 'Configure-NetworkGuard.ps1')

Write-Host '[3/5] Cloning application source...' -ForegroundColor Cyan
Write-Host ('      repository: {0}' -f $repositoryUrl)
Write-Host ('      ref:        {0}' -f $repositoryRef)

# --no-recurse-submodules is the default but is stated explicitly. Protocol overrides
# prevent a repository from redirecting Git toward local/file transports.
$cloneArgs = @(
    '-c','protocol.file.allow=never',
    '-c','protocol.ext.allow=never',
    '-c','credential.helper=',
    'clone',
    '--depth','1',
    '--single-branch',
    '--no-recurse-submodules',
    '--branch',$repositoryRef,
    '--',$repositoryUrl,$appRoot
)
& $git @cloneArgs
if ($LASTEXITCODE -ne 0) { throw "Git clone failed with exit code $LASTEXITCODE." }

if ((Test-Path -LiteralPath (Join-Path $appRoot '.gitmodules')) -and -not $allowSubmodules) {
    throw 'The cloned repository declares Git submodules, but AllowSubmodules is false. No submodule content was fetched.'
}

Push-Location $appRoot
try {
    $commit = (& $git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw 'Could not determine the cloned commit ID.'
    }
} finally {
    Pop-Location
}
Write-Host ('      commit:     {0}' -f $commit) -ForegroundColor Green

# EntrySource must remain inside C:\App even if config is accidentally malformed.
$sourceCandidate = [IO.Path]::GetFullPath((Join-Path $appRoot $entrySource))
$appPrefix = ([IO.Path]::GetFullPath($appRoot)).TrimEnd('\') + '\'
if (-not $sourceCandidate.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EntrySource resolves outside C:\App and was rejected.'
}
if (-not (Test-Path -LiteralPath $sourceCandidate -PathType Leaf)) {
    throw "Server source was not found in the cloned repository: $entrySource"
}

# Third-party executable is first invoked only after the network guard is active.
try {
    $ytVersion = (& $ytDlp --version 2>$null | Select-Object -First 1)
    if ($ytVersion) { Write-Host ('      yt-dlp version: {0}' -f $ytVersion) }
} catch {
    Write-Warning ('Could not query yt-dlp version: {0}' -f $_.Exception.Message)
}

$defaultRoute = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1
if (-not $defaultRoute) { throw 'No IPv4 default route was found inside Windows Sandbox.' }
$ipInfo = Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $defaultRoute.InterfaceIndex |
    Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } |
    Select-Object -First 1
if (-not $ipInfo) { throw 'Could not determine the Windows Sandbox IPv4 address.' }
$publicBaseUrl = 'http://{0}:{1}' -f $ipInfo.IPAddress,$port

Write-Host '[4/5] Compiling cloned application source inside Sandbox...' -ForegroundColor Cyan
$serverSource = Get-Content -LiteralPath $sourceCandidate -Raw
Add-Type -TypeDefinition $serverSource -Language CSharp -ReferencedAssemblies @(
    'System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll'
)

$urlFile = Join-Path ([Environment]::GetFolderPath('Desktop')) 'HERE-SPHERE-LINK-SERVER.txt'
@(
    'HereSphere Link Server for this Windows Sandbox session:'
    ''
    $publicBaseUrl
    ''
    ('Repository: {0}' -f $repositoryUrl)
    ('Ref:        {0}' -f $repositoryRef)
    ('Commit:     {0}' -f $commit)
    ''
    'The Sandbox IP can change on the next launch.'
    'Application source was cloned fresh into C:\App.'
    'Dependencies were read from the read-only host mapping.'
) | Set-Content -LiteralPath $urlFile -Encoding UTF8

$Host.UI.RawUI.WindowTitle = "HereSphere Sandbox - $publicBaseUrl"
Write-Host '[5/5] Ready.' -ForegroundColor Green
Write-Host ''
Write-Host 'HERE SPHERE LINK SERVER:' -ForegroundColor Yellow
Write-Host ('    {0}' -f $publicBaseUrl) -ForegroundColor Green
Write-Host ''
Write-Host ('SOURCE COMMIT: {0}' -f $commit) -ForegroundColor Cyan
Write-Host 'No dependency download or package installation was performed.'
Write-Host ''
try { Start-Process notepad.exe -ArgumentList ('"{0}"' -f $urlFile) | Out-Null } catch { }

[HereSphereSandbox.Server]::Run($port, $ytDlp, $publicBaseUrl)
