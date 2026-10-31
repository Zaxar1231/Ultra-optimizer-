<#
  Win11-Repair.ps1 - undo optimization tweaks (Ultimate-Optimizer / Win11-Gaming-Extreme / G15-Extreme)
  and repair Windows 10 / 11
  Run (PowerShell as Administrator):
      Set-ExecutionPolicy -Scope Process Bypass -Force; .\Win11-Repair.ps1
  Without the menu:  .\Win11-Repair.ps1 -Action Restore | Undo | Repair | Network | All
#>
param([ValidateSet('Menu','Restore','Undo','Repair','Network','All')][string]$Action = 'Menu')

#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference    = 'SilentlyContinue'

$GUI = [bool]$env:UO_GUI
function Step($t) { if ($GUI) { Write-Host "##step $t" } else { Write-Host "`n>>> $t" -ForegroundColor Cyan } }
function Ok($t)   { if ($GUI) { Write-Host "##ok $t" }   else { Write-Host "    $t" -ForegroundColor Green } }
function SetReg($path, $name, $value, $type = 'DWord') {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $type -Force | Out-Null
}
function DelReg($path, $name) { Remove-ItemProperty -Path $path -Name $name -Force }

# ---------------------------------------------------------------- MODULES
function Open-RestorePoint {
    Step "Opening System Restore - pick 'Before Ultimate-Optimizer' (or 'Before Win11-Gaming-Extreme' / 'Before G15-Extreme')"
    Start-Process rstrui.exe
}

function Undo-Tweaks {
    # Ultimate-Optimizer keeps an exact backup -> use it instead of generic defaults
    $ult = Join-Path $PSScriptRoot 'Ultimate-Optimizer.ps1'
    if ((Test-Path "$env:ProgramData\UltimateOptimizer\backup.json") -and (Test-Path $ult)) {
        Step "Exact backup from Ultimate-Optimizer found - restoring it"
        & $ult -Undo
        return
    }
    Step "Power: back to Balanced, hibernation/fast startup on"
    powercfg -setactive 381b4222-f694-41f0-9685-ff5bb260df2e
    powercfg -list | Select-String 'Gaming Extreme|G15 Extreme|Ultimate Optimizer' | ForEach-Object {
        $g = ([regex]'[0-9a-fA-F\-]{36}').Match($_).Value; if ($g) { powercfg -delete $g }
    }
    powercfg /h on
    SetReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 1
    DelReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' 'PowerThrottlingOff'

    Step "Scheduler / timer / MMCSS defaults"
    SetReg 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation' 2
    DelReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' 'GlobalTimerResolutionRequests'
    $mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
    SetReg $mm 'SystemResponsiveness' 20
    SetReg $mm 'NetworkThrottlingIndex' 10
    SetReg "$mm\Tasks\Games" 'GPU Priority' 8
    SetReg "$mm\Tasks\Games" 'Priority' 2
    SetReg "$mm\Tasks\Games" 'Scheduling Category' 'Medium' 'String'
    SetReg "$mm\Tasks\Games" 'SFIO Priority' 'Normal' 'String'
    bcdedit /deletevalue disabledynamictick | Out-Null
    DelReg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize' 'StartupDelayInMSec'

    Step "Graphics / Game DVR / FSO defaults"
    DelReg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR'
    SetReg 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled' 1
    SetReg 'HKCU:\System\GameConfigStore' 'GameDVR_FSEBehaviorMode' 0
    DelReg 'HKCU:\System\GameConfigStore' 'GameDVR_HonorUserFSEBehaviorMode'
    DelReg 'HKCU:\System\GameConfigStore' 'GameDVR_DXGIHonorFSEWindowsCompatible'

    Step "MSI interrupt mode -> driver default"
    Get-PnpDevice -PresentOnly | Where-Object { $_.Class -in 'Display','SCSIAdapter','HDC','Net' -and $_.InstanceId -like 'PCI\*' } | ForEach-Object {
        DelReg "HKLM:\SYSTEM\CurrentControlSet\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties" 'MSISupported'
    }
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' |
        Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object { DelReg $_.PSPath 'EnableUlps' }

    Step "Mouse acceleration on, Nagle default"
    SetReg 'HKCU:\Control Panel\Mouse' 'MouseSpeed' '1' 'String'
    SetReg 'HKCU:\Control Panel\Mouse' 'MouseThreshold1' '6' 'String'
    SetReg 'HKCU:\Control Panel\Mouse' 'MouseThreshold2' '10' 'String'
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces' | ForEach-Object {
        DelReg $_.PSPath 'TcpAckFrequency'; DelReg $_.PSPath 'TCPNoDelay'
    }

    Step "Background apps, visuals, policies"
    SetReg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 0
    DelReg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' 'BackgroundAppGlobalToggle'
    SetReg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' 'VisualFXSetting' 0
    SetReg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 1
    SetReg 'HKCU:\Control Panel\Desktop' 'MenuShowDelay' '400' 'String'
    DelReg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection' 'AllowTelemetry'
    DelReg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode'

    Step "Services back to default start type"
    $defaults = @{ 'DiagTrack'='Automatic'; 'dmwappushservice'='Manual'; 'MapsBroker'='Automatic'; 'WerSvc'='Manual';
                   'PcaSvc'='Automatic'; 'lfsvc'='Manual'; 'WpcMonSvc'='Manual'; 'Fax'='Manual'; 'NvTelemetryContainer'='Automatic' }
    foreach ($k in $defaults.Keys) { if (Get-Service $k) { Set-Service $k -StartupType $defaults[$k]; Ok "$k -> $($defaults[$k])" } }
    # Vendor services (Dell/HP/ASUS/Lenovo) -> Automatic again if they exist
    'SupportAssistAgent','DellClientManagementService','DellTechHub','DellOptimizer','ArmouryCrateService','AsusAppService',
    'LightingService','ImControllerService','LenovoVantageService','HPAppHelperCap','HPOmenCap' | ForEach-Object {
        if (Get-Service $_) { Set-Service $_ -StartupType Automatic; Ok "$_ -> Automatic" }
    }

    Step "Security back ON: VBS, hypervisor, CPU mitigations, RAM compression"
    bcdedit /set hypervisorlaunchtype auto | Out-Null
    DelReg 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' 'EnableVirtualizationBasedSecurity'
    DelReg 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled'
    DelReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'FeatureSettingsOverride'
    DelReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'FeatureSettingsOverrideMask'
    Enable-MMAgent -MemoryCompression
    Ok "done (Memory Integrity: turn on in Windows Security -> Device security if you want it)"
}

function Repair-System {
    Step "DISM RestoreHealth (5-20 min, needs internet)"
    Dism.exe /Online /Cleanup-Image /RestoreHealth
    Step "SFC scan"
    sfc /scannow
    Step "Re-registering built-in Store apps"
    Get-AppxPackage -AllUsers | ForEach-Object {
        if ($_.InstallLocation -and (Test-Path "$($_.InstallLocation)\AppXManifest.xml")) {
            Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml"
        }
    }
    Step "Reset Windows Update components"
    'wuauserv','bits','cryptsvc' | ForEach-Object { Stop-Service $_ -Force }
    Rename-Item "$env:SystemRoot\SoftwareDistribution" "SoftwareDistribution.old_$(Get-Date -f yyyyMMddHHmm)"
    'wuauserv','bits','cryptsvc' | ForEach-Object { Start-Service $_ }
    Step "Check disk (scheduled for next boot if C: is busy)"
    chkdsk.exe $env:SystemDrive /scan   # online scan, no prompt
}

function Reset-Network {
    Step "Network reset"
    netsh winsock reset | Out-Null
    netsh int ip reset  | Out-Null
    netsh int tcp set global autotuninglevel=normal | Out-Null
    ipconfig /release | Out-Null; ipconfig /flushdns | Out-Null; ipconfig /renew | Out-Null
    Ok "done (reboot needed)"
}

# ---------------------------------------------------------------- MENU
if ($Action -eq 'Menu') {
Write-Host @"

======= WINDOWS 10/11 REPAIR =======
 1) Open System Restore (full rollback to restore point)
 2) Undo optimization tweaks (exact backup if Ultimate-Optimizer was used)
 3) Repair Windows (DISM + SFC + apps + Update + disk)
 4) Reset network (no internet / high ping)
 5) EVERYTHING (2 + 3 + 4)
 0) Exit
====================================
"@ -ForegroundColor Magenta
    $Action = switch (Read-Host "Choose") { '1' { 'Restore' } '2' { 'Undo' } '3' { 'Repair' } '4' { 'Network' } '5' { 'All' } default { 'Exit' } }
}
switch ($Action) {
    'Restore' { Open-RestorePoint; exit }
    'Undo'    { Undo-Tweaks }
    'Repair'  { Repair-System }
    'Network' { Reset-Network }
    'All'     { Undo-Tweaks; Repair-System; Reset-Network }
    default   { exit }
}
Write-Host "`n========= DONE - REBOOT NOW =========" -ForegroundColor Green
Write-Host "Removed Store apps (Calculator, Photos etc.) can be reinstalled from Microsoft Store if needed." -ForegroundColor Yellow
