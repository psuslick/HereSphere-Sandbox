$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$group = 'HereSphere Sandbox Guard'
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

function IPv4ToUInt32([string]$Address) {
    $bytes = ([Net.IPAddress]::Parse($Address)).GetAddressBytes()
    [Array]::Reverse($bytes)
    return [BitConverter]::ToUInt32($bytes, 0)
}

function UInt32ToIPv4([UInt32]$Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    [Array]::Reverse($bytes)
    return (New-Object Net.IPAddress (,$bytes)).ToString()
}

function Get-SubnetRange([string]$Address, [int]$PrefixLength) {
    $ip = [UInt64](IPv4ToUInt32 $Address)
    if ($PrefixLength -eq 0) {
        $mask = [UInt64]0
    } else {
        $mask = ([UInt64]4294967295) -shl (32 - $PrefixLength)
        $mask = $mask -band [UInt64]4294967295
    }
    $start = $ip -band $mask
    $size = [UInt64][Math]::Pow(2, (32 - $PrefixLength))
    $end = $start + $size - 1
    return [pscustomobject]@{ Start=$start; End=$end }
}

function Subtract-Range([UInt64]$Start, [UInt64]$End, [UInt64]$ExceptStart, [UInt64]$ExceptEnd) {
    $out = @()
    if ($ExceptEnd -lt $Start -or $ExceptStart -gt $End) {
        return ,([pscustomobject]@{Start=$Start;End=$End})
    }
    if ($ExceptStart -gt $Start) {
        $out += [pscustomobject]@{Start=$Start;End=($ExceptStart - 1)}
    }
    if ($ExceptEnd -lt $End) {
        $out += [pscustomobject]@{Start=($ExceptEnd + 1);End=$End}
    }
    return $out
}

$defaultRoute = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Where-Object { $_.State -eq 'Alive' -or -not $_.State } |
    Sort-Object RouteMetric, InterfaceMetric |
    Select-Object -First 1
if (-not $defaultRoute) { throw 'No IPv4 default route was found inside Windows Sandbox.' }

$ipInfo = Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $defaultRoute.InterfaceIndex |
    Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } |
    Select-Object -First 1
if (-not $ipInfo) { throw 'Could not identify the Windows Sandbox IPv4 address.' }

$gateway = [string]$defaultRoute.NextHop
if (-not $gateway) { throw 'Could not identify the Windows Sandbox default gateway.' }

# DNS may be provided by the NAT gateway or by another resolver. Only DNS and
# DHCP ports need guest-initiated access to these addresses; the host initiates
# HereSphere connections to the guest, so that exchange needs an inbound rule.
$dnsAddresses = @(Get-DnsClientServerAddress -InterfaceIndex $defaultRoute.InterfaceIndex -AddressFamily IPv4 |
    ForEach-Object { $_.ServerAddresses } |
    Where-Object { $_ -and $_ -ne '127.0.0.1' } |
    Select-Object -Unique)
$exceptions = @($gateway, '255.255.255.255') + $dnsAddresses | Select-Object -Unique

# Block private/special IPv4 destinations, including the connected NAT subnet.
# Carve out only the gateway/DNS addresses and DHCP broadcast, then restrict ports.
$protected = @(
    @('0.0.0.0','0.255.255.255'),
    @('10.0.0.0','10.255.255.255'),
    @('100.64.0.0','100.127.255.255'),
    # Loopback addresses are internal to the disposable guest. The admin UI
    # requires guest-to-guest 127.0.0.1:5001 and is not reachable from host.
    @('169.254.0.0','169.254.255.255'),
    @('172.16.0.0','172.31.255.255'),
    @('192.0.0.0','192.0.0.255'),
    @('192.0.2.0','192.0.2.255'),
    @('192.168.0.0','192.168.255.255'),
    @('198.18.0.0','198.19.255.255'),
    @('198.51.100.0','198.51.100.255'),
    @('203.0.113.0','203.0.113.255'),
    @('224.0.0.0','255.255.255.255')
)
$connectedRange = Get-SubnetRange $ipInfo.IPAddress $ipInfo.PrefixLength
$protected += ,@((UInt32ToIPv4 ([UInt32]$connectedRange.Start)), (UInt32ToIPv4 ([UInt32]$connectedRange.End)))

$remoteRanges = @()
foreach ($r in $protected) {
    $start = [UInt64](IPv4ToUInt32 $r[0])
    $end = [UInt64](IPv4ToUInt32 $r[1])
    $pieces = @([pscustomobject]@{ Start=$start; End=$end })
    foreach ($address in $exceptions) {
        $value = [UInt64](IPv4ToUInt32 $address)
        $next = @()
        foreach ($piece in $pieces) {
            $next += @(Subtract-Range $piece.Start $piece.End $value $value)
        }
        $pieces = $next
    }
    foreach ($piece in $pieces) {
        $remoteRanges += ('{0}-{1}' -f (UInt32ToIPv4 ([UInt32]$piece.Start)), (UInt32ToIPv4 ([UInt32]$piece.End)))
    }
}

New-NetFirewallRule -DisplayName 'HereSphere Sandbox - Block private IPv4 egress' -Group $group `
    -Direction Outbound -Action Block -Profile Any -RemoteAddress $remoteRanges | Out-Null

foreach ($address in $exceptions) {
    $blockedPorts = if ($address -eq $gateway) { @('1-52','54-66','68-65535') }
                    elseif ($address -eq '255.255.255.255') { @('1-66','68-65535') }
                    else { @('1-52','54-65535') }
    foreach ($protocol in @('TCP','UDP')) {
        New-NetFirewallRule -DisplayName "HereSphere Sandbox - Restrict $protocol to $address" -Group $group `
            -Direction Outbound -Action Block -Profile Any -Protocol $protocol `
            -RemoteAddress $address -RemotePort $blockedPorts | Out-Null
    }
    New-NetFirewallRule -DisplayName "HereSphere Sandbox - Block ICMP to $address" -Group $group `
        -Direction Outbound -Action Block -Profile Any -Protocol ICMPv4 -RemoteAddress $address | Out-Null
}

New-NetFirewallRule -DisplayName 'HereSphere Sandbox - Block private IPv6 egress' -Group $group `
    -Direction Outbound -Action Block -Profile Any -RemoteAddress @('fc00::/7','fe80::/10','2001:db8::/32') | Out-Null

# Server ingress is permitted only from the Sandbox NAT gateway (the Windows host side of this isolated virtual subnet).
New-NetFirewallRule -DisplayName 'HereSphere Sandbox - Allow host to TCP 5000' -Group $group `
    -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 5000 -RemoteAddress $gateway | Out-Null

Write-Host ('Network guard active. Sandbox={0}/{1}, gateway={2}, DNS={3}' -f $ipInfo.IPAddress, $ipInfo.PrefixLength, $gateway, ($dnsAddresses -join ','))
