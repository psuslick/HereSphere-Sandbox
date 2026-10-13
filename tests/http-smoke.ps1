$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$sourcePath = Join-Path $repository 'src\HereSphereSandboxServer.cs'
$port = 25137
$baseUrl = "http://127.0.0.1:$port"
$serverScript = Join-Path $repository 'tests\serve-test.ps1'
$arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" "{1}" {2}' -f $serverScript,$sourcePath,$port
$server = Start-Process -FilePath 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
    -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 2
            if ($health.success) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Server did not become ready.' }

    $blocked = Invoke-RestMethod -Uri "$baseUrl/stream" -Method Post -ContentType 'application/json' `
        -Body '{"url":"http://127.0.0.1/private.mp4"}'
    if ($blocked.success) { throw 'Private URL was accepted.' }

    $direct = Invoke-RestMethod -Uri "$baseUrl/stream" -Method Post -ContentType 'application/json' `
        -Body '{"url":"https://1.1.1.1/video.mp4?mode=test"}'
    if (-not $direct.success -or $direct.videoUrl -notmatch "^$([regex]::Escape($baseUrl))/media/[0-9a-f]{32}$") {
        throw 'Direct public URL did not return an opaque media URL.'
    }

    $crossOriginBlocked = $false
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/health" -Headers @{ Origin='https://example.com' } | Out-Null
    } catch {
        $crossOriginBlocked = ($_.Exception.Response.StatusCode.value__ -eq 403)
    }
    if (-not $crossOriginBlocked) { throw 'Cross-origin browser request was allowed.' }
    Write-Output 'HTTP checks passed: health, private URL rejection, direct proxy URL, cross-origin rejection.'
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
