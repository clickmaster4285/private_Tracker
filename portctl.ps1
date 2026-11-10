<#
  portctl.ps1 - Hardware & Port Control test harness (Windows)
  Standalone proof of the scan + block mechanism before it is wired into the DlpService.

  USAGE (run from an ELEVATED PowerShell for block/unblock; scan/status work unprivileged):

    powershell -ExecutionPolicy Bypass -File portctl.ps1 scan
        List every port/device across the 3 categories, each with an index + InstanceId.

    powershell -ExecutionPolicy Bypass -File portctl.ps1 status -Id "<InstanceId>"
        Show one device's present/enabled/problem state.

    powershell -ExecutionPolicy Bypass -File portctl.ps1 block   -Id "<InstanceId>" [-Force]
    powershell -ExecutionPolicy Bypass -File portctl.ps1 unblock -Id "<InstanceId>"
        Disable / re-enable one device. Risky classes (disks, keyboard, mouse, display,
        net) require -Force. The boot/system disk is ALWAYS refused.

  Everything is reversible with 'unblock'. Nothing here is permanent.
#>

[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('scan','status','block','unblock')]
  [string]$Action = 'scan',

  [string]$Id,
  [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Test-Admin {
  $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
  return $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Instance ids of the boot/system physical disk(s) - NEVER block these.
function Get-ProtectedDiskInstanceIds {
  $ids = @()
  try {
    $sysDisks = Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.IsBoot -or $_.IsSystem }
    foreach ($d in $sysDisks) {
      # Match the PnP DiskDrive by model + serial fragment.
      $pd = Get-PnpDevice -Class DiskDrive -PresentOnly -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -and $d.FriendlyName -and $_.FriendlyName -like "*$($d.FriendlyName)*" }
      $ids += ($pd | Select-Object -Expand InstanceId)
    }
  } catch {}
  return $ids
}

$RiskyClasses = @('DiskDrive','Keyboard','Mouse','HIDClass','Display','Monitor','Net','NetworkAdapter')

function Invoke-Scan {
  Write-Host "`n==== CATEGORY 1 - EXTERNAL PHYSICAL PORTS ====" -ForegroundColor Cyan

  Write-Host "`n[USB] controllers:" -ForegroundColor Yellow
  Get-CimInstance Win32_USBController | Select-Object -Expand Name -Unique | ForEach-Object { "   $_" }

  Write-Host "`n[VIDEO OUT] GPU + attached monitor connector:" -ForegroundColor Yellow
  Get-CimInstance Win32_VideoController | ForEach-Object { "   GPU: $($_.Name)" }
  Get-CimInstance -Namespace root\wmi WmiMonitorConnectionParams -EA SilentlyContinue | ForEach-Object {
    $t = switch ($_.VideoOutputTechnology) { 0 {'VGA'} 4 {'DVI'} 5 {'HDMI'} 10 {'DisplayPort'} 11 {'eDP(internal)'} default {"tech$($_.VideoOutputTechnology)"} }
    "   Monitor on: $t   (detect/alert only - not hard-blockable)"
  }

  Write-Host "`n[AUDIO] endpoints:" -ForegroundColor Yellow
  Get-CimInstance Win32_SoundDevice | ForEach-Object { "   $($_.Name)  [$($_.Status)]" }

  Write-Host "`n[SERIAL/PARALLEL]:" -ForegroundColor Yellow
  (Get-CimInstance Win32_SerialPort   | ForEach-Object { "   COM: $($_.DeviceID) $($_.Name)" })
  (Get-CimInstance Win32_ParallelPort | ForEach-Object { "   LPT: $($_.Name)" })

  Write-Host "`n==== CATEGORY 2 - INTERNAL INTERFACES ====" -ForegroundColor Cyan
  Write-Host "`n[DISKS] bus type:" -ForegroundColor Yellow
  Get-CimInstance -Namespace root\Microsoft\Windows\Storage MSFT_PhysicalDisk -EA SilentlyContinue | ForEach-Object {
    $bus = switch ($_.BusType) { 17 {'NVMe'} 11 {'SATA'} 7 {'USB'} 8 {'RAID'} 10 {'SAS'} default {"Bus$($_.BusType)"} }
    "   $($_.FriendlyName) | $bus | {0:N0} GB" -f ($_.Size/1GB)
  }
  Write-Host "`n[STORAGE CONTROLLERS / PCIe classes]:" -ForegroundColor Yellow
  Get-PnpDevice -PresentOnly -EA SilentlyContinue | Where-Object { $_.InstanceId -like 'PCI\*' } |
    Group-Object Class | Sort-Object Count -Descending | ForEach-Object { "   {0,-18} x{1}" -f $_.Name,$_.Count }

  Write-Host "`n==== CATEGORY 3 - WIRELESS & VIRTUAL ====" -ForegroundColor Cyan
  Get-CimInstance Win32_NetworkAdapter | Where-Object { $_.PNPDeviceID } | ForEach-Object {
    $k = if ($_.PhysicalAdapter) {'PHYS'} else {'VIRT'}
    "   [$k] $($_.Name)"
  }
  Get-PnpDevice -PresentOnly -Class Bluetooth -EA SilentlyContinue | ForEach-Object { "   [BT] $($_.FriendlyName)" }

  Write-Host "`n==== BLOCKABLE DEVICES (indexed - copy an InstanceId to block/unblock) ====" -ForegroundColor Green
  $protected = Get-ProtectedDiskInstanceIds
  $i = 0
  Get-PnpDevice -PresentOnly -EA SilentlyContinue |
    Where-Object { $_.InstanceId -notlike 'ROOT\*' -and $_.Class } |
    Sort-Object Class, FriendlyName | ForEach-Object {
      $i++
      $flag = ''
      if ($protected -contains $_.InstanceId) { $flag = '  <PROTECTED boot/system disk>' }
      elseif ($RiskyClasses -contains $_.Class) { $flag = '  <risky: needs -Force>' }
      "{0,3}. [{1,-16}] {2}{3}" -f $i, $_.Class, $_.FriendlyName, $flag
      "      Id: $($_.InstanceId)"
    }
  Write-Host "`nBlock:   portctl.ps1 block   -Id `"<InstanceId>`"  [-Force]"
  Write-Host "Unblock: portctl.ps1 unblock -Id `"<InstanceId>`""
}

function Get-Dev([string]$instanceId) {
  $d = Get-PnpDevice -InstanceId $instanceId -EA SilentlyContinue
  if (-not $d) { throw "No device with InstanceId '$instanceId'. Run 'scan' to list ids." }
  return $d
}

function Invoke-Status([string]$instanceId) {
  $d = Get-Dev $instanceId
  "Name   : $($d.FriendlyName)"
  "Class  : $($d.Class)"
  "Status : $($d.Status)        Present: $($d.Present)"
  "Problem: $($d.ProblemDescription)"
  "Id     : $($d.InstanceId)"
}

function Assert-Safe([object]$d) {
  if ((Get-ProtectedDiskInstanceIds) -contains $d.InstanceId) {
    throw "REFUSED: '$($d.FriendlyName)' is the boot/system disk - blocking it would make the PC unbootable."
  }
  if (($RiskyClasses -contains $d.Class) -and -not $Force) {
    throw "'$($d.FriendlyName)' is class '$($d.Class)' (risky). Re-run with -Force if you are sure. It is reversible with 'unblock'."
  }
}

function Invoke-Block([string]$instanceId) {
  if (-not (Test-Admin)) { throw "block/unblock require an ELEVATED PowerShell (Run as administrator)." }
  $d = Get-Dev $instanceId
  Assert-Safe $d
  Write-Host "Disabling: $($d.FriendlyName) [$($d.Class)]" -ForegroundColor Red
  Disable-PnpDevice -InstanceId $instanceId -Confirm:$false
  Start-Sleep -Milliseconds 400
  Invoke-Status $instanceId
  Write-Host "BLOCKED. Reverse with:  portctl.ps1 unblock -Id `"$instanceId`"" -ForegroundColor Yellow
}

function Invoke-Unblock([string]$instanceId) {
  if (-not (Test-Admin)) { throw "block/unblock require an ELEVATED PowerShell (Run as administrator)." }
  $d = Get-Dev $instanceId
  Write-Host "Enabling: $($d.FriendlyName) [$($d.Class)]" -ForegroundColor Green
  Enable-PnpDevice -InstanceId $instanceId -Confirm:$false
  Start-Sleep -Milliseconds 400
  Invoke-Status $instanceId
}

switch ($Action) {
  'scan'    { Invoke-Scan }
  'status'  { if (-not $Id) { throw "status needs -Id '<InstanceId>'" }; Invoke-Status $Id }
  'block'   { if (-not $Id) { throw "block needs -Id '<InstanceId>'" };  Invoke-Block  $Id }
  'unblock' { if (-not $Id) { throw "unblock needs -Id '<InstanceId>'" };Invoke-Unblock $Id }
}
