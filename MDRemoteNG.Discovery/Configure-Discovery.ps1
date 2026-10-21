[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Firewall')]
    [string]$Action = 'Install',
    [string]$HelperPath = (Join-Path $PSScriptRoot 'MDRemoteNG.Discovery.exe')
)
$ErrorActionPreference = 'Stop'
$helperExecutable = [System.IO.Path]::GetFullPath($HelperPath)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
switch ($Action) {
    'Install' {
        if (-not (Test-Path -LiteralPath $helperExecutable -PathType Leaf)) { throw "Helper not found: $helperExecutable" }
        New-Item -Path $runKey -Force | Out-Null
        New-ItemProperty -Path $runKey -Name 'MDRemoteNGDiscovery' -Value ('"' + $helperExecutable + '" --helper') -PropertyType String -Force | Out-Null
        Start-Process -FilePath $helperExecutable -ArgumentList '--helper' -WindowStyle Hidden
    }
    'Uninstall' {
        Remove-ItemProperty -Path $runKey -Name 'MDRemoteNGDiscovery' -ErrorAction SilentlyContinue
        Get-Process -Name 'MDRemoteNG.Discovery' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and [string]::Equals($_.Path, $helperExecutable, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process
    }
    'Firewall' {
        if (-not (Test-Path -LiteralPath $helperExecutable -PathType Leaf)) { throw "Helper not found: $helperExecutable" }
        # Run this action in an elevated PowerShell only when Windows Firewall requires it.
        # Rules are limited to this executable, the local subnet, and Private/Domain profiles.
        foreach ($rule in @('MDRemoteNG Discovery UDP', 'MDRemoteNG Discovery TCP')) {
            Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        }
        New-NetFirewallRule -DisplayName 'MDRemoteNG Discovery UDP' -Direction Inbound -Action Allow -Program $helperExecutable -Protocol UDP -LocalPort 48765 -RemoteAddress LocalSubnet -Profile Private,Domain | Out-Null
        New-NetFirewallRule -DisplayName 'MDRemoteNG Discovery TCP' -Direction Inbound -Action Allow -Program $helperExecutable -Protocol TCP -RemoteAddress LocalSubnet -Profile Private,Domain | Out-Null
    }
}
