$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Run once on the host from a reviewed checkout. Never run this from Sandbox.
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$destination = 'C:\SandboxBootstrap\HereSphere'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'bootstrap.ps1') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $source 'Configure-NetworkGuard.ps1') -Destination $destination -Force

$config = Join-Path $destination 'repo-config.psd1'
if (-not (Test-Path -LiteralPath $config)) {
    Copy-Item -LiteralPath (Join-Path $source 'repo-config.example.psd1') -Destination $config
}

$sourceDir = Join-Path $destination 'Sources'
New-Item -ItemType Directory -Force -Path $sourceDir | Out-Null
foreach ($file in @('videos.txt','websites.txt')) {
    $path = Join-Path $sourceDir $file
    if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType File -Path $path | Out-Null }
}
Write-Host "Bootstrap installed in $destination"
Write-Host "Edit $config with your public HTTPS repository URL."
Write-Host 'Place portable Git and yt-dlp in C:\SandboxBootstrap as described in README.md.'
