$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module NetSecurity

$script:rules = New-Object System.Collections.ArrayList
function Get-NetFirewallRule { param($Group, $ErrorAction) }
function Get-NetRoute { param($AddressFamily, $DestinationPrefix)
    [pscustomobject]@{ State='Alive'; RouteMetric=1; InterfaceMetric=1; InterfaceIndex=7; NextHop='172.28.64.1' }
}
function Get-NetIPAddress { param($AddressFamily, $InterfaceIndex)
    [pscustomobject]@{ IPAddress='172.28.64.3'; PrefixLength=20 }
}
function Get-DnsClientServerAddress { param($InterfaceIndex, $AddressFamily)
    [pscustomobject]@{ ServerAddresses=@('172.28.64.1','10.0.0.53') }
}
function New-NetFirewallRule {
    param($DisplayName,$Group,$Direction,$Action,$Profile,$RemoteAddress,$Protocol,$RemotePort,$LocalPort)
    [void]$script:rules.Add([pscustomobject]@{
        Name=$DisplayName; Direction=$Direction; RemoteAddress=@($RemoteAddress);
        Protocol=$Protocol; RemotePort=@($RemotePort); LocalPort=$LocalPort
    })
}

$repository = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $repository 'host\Configure-NetworkGuard.ps1')

$block = $script:rules | Where-Object { $_.Name -eq 'HereSphere Sandbox - Block private IPv4 egress' }
if (-not $block) { throw 'Missing IPv4 block rule.' }
function Is-InRanges([string]$Address, [string[]]$Ranges) {
    $value = [UInt64](IPv4ToUInt32 $Address)
    foreach ($range in $Ranges) {
        $parts = $range.Split('-')
        if ($value -ge [UInt64](IPv4ToUInt32 $parts[0]) -and $value -le [UInt64](IPv4ToUInt32 $parts[1])) { return $true }
    }
    return $false
}
foreach ($address in @('172.28.64.20','192.168.1.1','10.0.0.1')) {
    if (-not (Is-InRanges $address $block.RemoteAddress)) { throw "Expected $address to be blocked." }
}
foreach ($address in @('172.28.64.1','10.0.0.53','1.1.1.1')) {
    if (Is-InRanges $address $block.RemoteAddress) { throw "Expected $address to be outside the blanket block." }
}
foreach ($address in @('172.28.64.1','10.0.0.53')) {
    $restricted = @($script:rules | Where-Object { $_.Name -eq "HereSphere Sandbox - Restrict TCP to $address" })
    $expectedRange = if ($address -eq '172.28.64.1') { '68-65535' } else { '54-65535' }
    if ($restricted.Count -ne 1 -or $restricted[0].RemotePort -notcontains $expectedRange) {
        throw "Missing port restriction for $address."
    }
}
$ingress = @($script:rules | Where-Object { $_.Direction -eq 'Inbound' -and $_.LocalPort -eq 5000 })
if ($ingress.Count -ne 1 -or $ingress[0].RemoteAddress -notcontains '172.28.64.1') {
    throw 'Host-only ingress rule is wrong.'
}
Write-Output 'Network guard checks passed: NAT peer blocked, DNS endpoints restricted, host ingress scoped.'
