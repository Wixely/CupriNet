#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stops and removes the CupriNet Lodestar Windows service.
.DESCRIPTION
  Does not delete the data directory (C:\ProgramData\CupriNet.Lodestar) — remove that by hand if you want to
  discard the node's identity and known-peer cache.
#>
param(
    [string]$ServiceName = "CupriNetLodestar"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

# Remove-Service exists in PowerShell 7+; fall back to sc.exe on Windows PowerShell 5.1.
if (Get-Command Remove-Service -ErrorAction SilentlyContinue) {
    Remove-Service -Name $ServiceName
} else {
    & sc.exe delete $ServiceName | Out-Null
}

Write-Host "Removed service '$ServiceName'. Data (identity + peer cache) is left in C:\ProgramData\CupriNet.Lodestar."
