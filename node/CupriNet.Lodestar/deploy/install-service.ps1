#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the CupriNet Lodestar as a Windows service.

.DESCRIPTION
  Registers cuprinet-lodestar.exe (which auto-detects the Windows service host) as an automatic-start service.
  Run this from the folder that contains cuprinet-lodestar.exe, in an elevated PowerShell.

  Configuration: the service reads CUPRINET_LODESTAR_* machine environment variables and/or the appsettings.json
  next to the exe. At minimum set the network id. This script sets CUPRINET_LODESTAR_Concordium at machine scope
  when you pass -Concordium.

  The node's own connection link is written to  C:\ProgramData\CupriNet.Lodestar\lodestar.link  (the default data
  directory), and its logs go to the Windows Event Log (source "CupriNet Lodestar").

.EXAMPLE
  .\install-service.ps1 -Concordium example.chat
#>
param(
    [string]$ServiceName = "CupriNetLodestar",
    [string]$DisplayName = "CupriNet Lodestar",
    [string]$Concordium
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $PSScriptRoot "cuprinet-lodestar.exe"
if (-not (Test-Path $exe)) {
    throw "cuprinet-lodestar.exe was not found next to this script. Run it from the published Lodestar folder."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists. Remove it first with uninstall-service.ps1."
    return
}

# Persist the network id at machine scope so the service (a separate session) sees it. Set any other
# CUPRINET_LODESTAR_* values the same way, e.g.:
#   [Environment]::SetEnvironmentVariable('CUPRINET_LODESTAR_PublicHost','lodestar.example.net','Machine')
if ($Concordium) {
    [Environment]::SetEnvironmentVariable("CUPRINET_LODESTAR_Concordium", $Concordium, "Machine")
    Write-Host "Set machine env CUPRINET_LODESTAR_Concordium=$Concordium"
}

New-Service -Name $ServiceName `
    -BinaryPathName "`"$exe`"" `
    -DisplayName $DisplayName `
    -StartupType Automatic `
    -Description "CupriNet Lodestar - keeps the overlay network alive (Layer 1 only, no channel content)."

Start-Service -Name $ServiceName
Write-Host "Installed and started '$ServiceName'."
Write-Host "Link file: C:\ProgramData\CupriNet.Lodestar\lodestar.link"
Write-Host "Logs:      Get-EventLog -LogName Application -Source 'CupriNet Lodestar' -Newest 20"
