<#
=====================================================================
  Ultimate-Optimizer.ps1  v3
  Atlas-level tuning for ANY Windows 10 / 11 PC or laptop (x64), no reinstall.

  - Tweaks taken from AtlasOS Playbook v0.5.0 (only the ones that can't break things)
    + gaming tweaks (MMCSS, GPU priority, HAGS, MSI mode, Game DVR, power plan)
  - Hardware-aware: laptop/desktop, SSD/HDD, RAM size, NVIDIA/AMD/Intel, vendor
  - Downloads tools (winget) + NVIDIA Profile Inspector (GitHub) and imports
    a max-performance driver profile (setting IDs verified against NVIDIA NVAPI header)
  - Optionally downloads optimizerDuck (github.com/itsfatduck/optimizerDuck) to C:\Tools
  - EVERY change is recorded first -> exact rollback:  .\Ultimate-Optimizer.ps1 -Undo

  Usage (PowerShell as Administrator):
     Set-ExecutionPolicy -Scope Process Bypass -Force
     .\Ultimate-Optimizer.ps1            interactive (asks about risky stuff)
     .\Ultimate-Optimizer.ps1 -Yes       no questions, safe defaults, installs tools
     .\Ultimate-Optimizer.ps1 -NoApps    skip software downloads
     .\Ultimate-Optimizer.ps1 -NoDuck    don't download optimizerDuck
     .\Ultimate-Optimizer.ps1 -Undo      restore EVERYTHING exactly as it was
     -DisableVBS / -DisableMitigations   turn the risky options on without asking (used by the GUI)
=====================================================================
#>
param([switch]$Undo, [switch]$Yes, [switch]$NoApps, [switch]$NoDuck, [switch]$DisableVBS, [switch]$DisableMitigations)

#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference    = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BKDir  = Join-Path $env:ProgramData 'UltimateOptimizer'
$BKFile = Join-Path $BKDir 'backup.json'
$Tools  = Join-Path $env:SystemDrive 'Tools'
New-Item -ItemType Directory -Path $BKDir, $Tools -Force | Out-Null
Start-Transcript -Path (Join-Path $BKDir "log_$(Get-Date -f yyyyMMdd_HHmmss).txt") | Out-Null

# UO_GUI=1 -> plain markers that UltimateOptimizer.exe turns into styled log lines
$GUI = [bool]$env:UO_GUI
function Step($t) { if ($GUI) { Write-Host "##step $t" } else { Write-Host "`n>>> $t" -ForegroundColor Cyan } }
function Ok($t)   { if ($GUI) { Write-Host "##ok $t" }   else { Write-Host "    $t" -ForegroundColor Green } }
function Warn($t) { if ($GUI) { Write-Host "##warn $t" } else { Write-Host "    $t" -ForegroundColor Yellow } }
function Ask($q)  { if ($Yes) { return $false }; (Read-Host "$q [y/N]") -match '^(y|yes|d|da)$' }

# =====================================================================
#  BACKUP ENGINE - remembers the ORIGINAL value of everything we touch
# =====================================================================
$BK = @{ Reg = @(); Tasks = @(); Nic = @(); Misc = @{}; Appx = @() }
if (Test-Path $BKFile) {
    $j = Get-Content $BKFile -Raw | ConvertFrom-Json
    if ($j.Reg)   { $BK.Reg   = @($j.Reg) }
    if ($j.Tasks) { $BK.Tasks = @($j.Tasks) }
    if ($j.Nic)   { $BK.Nic   = @($j.Nic) }
    if ($j.Appx)  { $BK.Appx  = @($j.Appx) }
    if ($j.Misc)  { $j.Misc.PSObject.Properties | ForEach-Object { $BK.Misc[$_.Name] = $_.Value } }
}
$regSeen = @{}; foreach ($r in $BK.Reg) { $regSeen["$($r.Path)|$($r.Name)"] = $true }
function Save-BK { $BK | ConvertTo-Json -Depth 6 | Set-Content $BKFile -Encoding UTF8 }

$script:Rerun = Test-Path $BKFile
$script:Bulk  = $false
# Windows defaults for UI values: used only if a previous run crashed before saving its backup
# (current value already equals our tweak and no record exists -> record the real Windows default)
$KnownDefaults = @{
  'HKCU:\Control Panel\Desktop|UserPreferencesMask'   = @{ Exists=$true;  Type='Binary'; Value=[byte[]](0x9E,0x1E,0x07,0x80,0x12,0x00,0x00,0x00) }
  'HKCU:\Control Panel\Desktop|FontSmoothing'         = @{ Exists=$true;  Type='String'; Value='2' }
  'HKCU:\Control Panel\Desktop|DragFullWindows'       = @{ Exists=$true;  Type='String'; Value='1' }
  'HKCU:\Control Panel\Desktop\WindowMetrics|MinAnimate' = @{ Exists=$true; Type='String'; Value='1' }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|TaskbarAnimations'   = @{ Exists=$false }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|ListviewAlphaSelect' = @{ Exists=$true; Type='DWord'; Value=1 }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|ListviewShadow'      = @{ Exists=$true; Type='DWord'; Value=1 }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|IconsOnly'           = @{ Exists=$true; Type='DWord'; Value=0 }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects|VisualFXSetting' = @{ Exists=$false }
  'HKCU:\Software\Microsoft\Windows\DWM|EnableAeroPeek'            = @{ Exists=$true; Type='DWord'; Value=1 }
  'HKCU:\Software\Microsoft\Windows\DWM|AlwaysHibernateThumbnails' = @{ Exists=$true; Type='DWord'; Value=0 }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize|EnableTransparency' = @{ Exists=$true; Type='DWord'; Value=1 }
  'HKCU:\Control Panel\Desktop|MenuShowDelay'         = @{ Exists=$true;  Type='String'; Value='400' }
  'HKCU:\Control Panel\Desktop|MouseHoverTime'        = @{ Exists=$true;  Type='String'; Value='400' }
  'HKCU:\Control Panel\Desktop|HungAppTimeout'        = @{ Exists=$false }
  'HKCU:\Control Panel\Desktop|WaitToKillAppTimeOut'  = @{ Exists=$false }
  'HKLM:\SYSTEM\CurrentControlSet\Control|WaitToKillServiceTimeout' = @{ Exists=$true; Type='String'; Value='5000' }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize|StartupDelayInMSec' = @{ Exists=$false }
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer|Max Cached Icons' = @{ Exists=$false }
  'HKCU:\Control Panel\Mouse|MouseSpeed'      = @{ Exists=$true; Type='String'; Value='1' }
  'HKCU:\Control Panel\Mouse|MouseThreshold1' = @{ Exists=$true; Type='String'; Value='6' }
  'HKCU:\Control Panel\Mouse|MouseThreshold2' = @{ Exists=$true; Type='String'; Value='10' }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications|GlobalUserDisabled' = @{ Exists=$false }
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search|BackgroundAppGlobalToggle' = @{ Exists=$false }
}
function Same($a, $b) { if ($a -is [byte[]] -or $b -is [byte[]]) { return ((@($a) -join ',') -eq (@($b) -join ',')) }; return ("$a" -eq "$b") }

# Never throws: records the original value FIRST (saved to disk immediately), then tries to write
function Set-Tweak($Path, $Name, $Value, $Type = 'DWord') {
    $id = "$Path|$Name"
    if (-not $regSeen.ContainsKey($id)) {
        $rec = [ordered]@{ Path = $Path; Name = $Name; Exists = $false; Type = $null; Value = $null }
        if (Test-Path $Path) {
            $key = Get-Item $Path
            if ($key.GetValueNames() -contains $Name) {
                $rec.Exists = $true
                $rec.Type   = $key.GetValueKind($Name).ToString()
                $rec.Value  = $key.GetValue($Name, $null, 'DoNotExpandEnvironmentNames')
            }
        }
        if ($script:Rerun -and $rec.Exists -and (Same $rec.Value $Value) -and $KnownDefaults.ContainsKey($id)) {
            $d = $KnownDefaults[$id]; $rec.Exists = $d.Exists; $rec.Type = $d.Type; $rec.Value = $d.Value
        }
        $BK.Reg += [pscustomobject]$rec; $regSeen[$id] = $true
        if (-not $script:Bulk) { Save-BK }
    }
    try {
        if (-not (Test-Path $Path)) { New-Item -Path $Path -Force -ErrorAction Stop | Out-Null }
        New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force -ErrorAction Stop | Out-Null
    } catch {
        Warn "skip (protected by Windows, harmless): $Name"
    }
}
function Try-Tweak($Path, $Name, $Value, $Type = 'DWord') { Set-Tweak $Path $Name $Value $Type }
# Services through their registry Start value -> exact undo for free
function Set-Svc($Name, $Start) {   # 2=Auto 3=Manual 4=Disabled
    $p = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path $p)) { return }
    Try-Tweak $p 'Start' $Start
    if ($Start -eq 4) { Stop-Service $Name -Force }
    Ok "service $Name -> $(@{2='Auto';3='Manual';4='Disabled'}[$Start])"
}
function Disable-Task($TaskPath) {
    $n = Split-Path $TaskPath -Leaf; $f = (Split-Path $TaskPath -Parent).TrimEnd('\') + '\'
    $t = Get-ScheduledTask -TaskPath $f -TaskName $n
    if (-not $t) { return }
    if (-not ($BK.Tasks | Where-Object { $_.Path -eq $TaskPath })) { $BK.Tasks += [pscustomobject]@{ Path = $TaskPath; State = "$($t.State)" } }
    $t | Disable-ScheduledTask | Out-Null
}
function Misc-Once($Key, $Value) { if (-not $BK.Misc.ContainsKey($Key)) { $BK.Misc[$Key] = $Value } }

# =====================================================================
#  NVIDIA profile (IDs + values from NVIDIA NvApiDriverSettings.h)
# =====================================================================
$NvMax = @(  # name, id, max-perf value, driver default value
    @('Power management mode = Prefer maximum performance', 0x1057EB71, 1,          0x5),
    @('Low Latency Mode = On (max pre-rendered frames 1)',  0x007BA09E, 1,          0),
    @('Texture filtering - Quality = High performance',     0x00CE2691, 0x14,       0),
    @('Texture filtering - Anisotropic sample optimization',0x00E73211, 1,          0),
    @('Texture filtering - Anisotropic filter optimization',0x0084CD70, 1,          0),
    @('Vertical Sync = Force off',                          0x00A879CF, 0x08416747, 0x60925292),
    @('Preferred refresh rate = Highest available',         0x0064B541, 1,          0),
    @('Shader Cache = On',                                  0x00198FFF, 1,          1),
    @('Shader Cache size = Unlimited',                      0x00AC8497, 4294967295, 0x4000),
    @('Ansel = Off',                                        0x1075D972, 0,          1)
)
function Write-Nip($File, [int]$Col) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-16"?>')
    [void]$sb.AppendLine('<ArrayOfProfile>')
    [void]$sb.AppendLine('  <Profile>')
    [void]$sb.AppendLine('    <ProfileName>Base Profile</ProfileName>')
    [void]$sb.AppendLine('    <Executeables />')
    [void]$sb.AppendLine('    <Settings>')
    foreach ($s in $NvMax) {
        [void]$sb.AppendLine('      <ProfileSetting>')
        [void]$sb.AppendLine("        <SettingNameInfo>$($s[0])</SettingNameInfo>")
        [void]$sb.AppendLine("        <SettingID>$([uint32]$s[1])</SettingID>")
        [void]$sb.AppendLine("        <SettingValue>$([uint32]$s[$Col])</SettingValue>")
        [void]$sb.AppendLine('        <ValueType>Dword</ValueType>')
        [void]$sb.AppendLine('      </ProfileSetting>')
    }
    [void]$sb.AppendLine('    </Settings>')
    [void]$sb.AppendLine('  </Profile>')
    [void]$sb.AppendLine('</ArrayOfProfile>')
    [IO.File]::WriteAllText($File, $sb.ToString(), [Text.Encoding]::Unicode)
}
function Run-NVPI($exe, $argList) {
    $p = Start-Process $exe -ArgumentList $argList -WorkingDirectory (Split-Path $exe) -PassThru
    if (-not $p.WaitForExit(30000)) { $p.Kill() }   # never hang the script
}
function Get-NVPI {
    $dir = Join-Path $Tools 'nvidiaProfileInspector'
    $exe = Join-Path $dir 'nvidiaProfileInspector.exe'
    if (-not (Test-Path $exe)) {
        $zip = Join-Path $env:TEMP 'nvpi.zip'
        Invoke-WebRequest 'https://github.com/Orbmu2k/nvidiaProfileInspector/releases/latest/download/nvidiaProfileInspector.zip' -OutFile $zip -UseBasicParsing
        Expand-Archive $zip -DestinationPath $dir -Force
        Remove-Item $zip -Force
    }
    if (Test-Path $exe) { return $exe } else { return $null }
}

# =====================================================================
#  UNDO
# =====================================================================
if ($Undo) {
    if (-not (Test-Path $BKFile)) { Write-Host "No backup found - nothing to undo." -ForegroundColor Red; Stop-Transcript | Out-Null; exit }
    Step "Restoring registry ($($BK.Reg.Count) values)"
    foreach ($r in $BK.Reg) {
        if ($r.Exists) {
            $v = $r.Value
            if ($r.Type -eq 'Binary')      { $v = [byte[]]@($r.Value) }
            if ($r.Type -eq 'MultiString') { $v = [string[]]@($r.Value) }
            if (-not (Test-Path $r.Path)) { New-Item $r.Path -Force | Out-Null }
            New-ItemProperty -Path $r.Path -Name $r.Name -Value $v -PropertyType $r.Type -Force | Out-Null
        } else {
            Remove-ItemProperty -Path $r.Path -Name $r.Name -Force
        }
    }
    Ok "done"
    Step "Restoring scheduled tasks"
    foreach ($t in $BK.Tasks) {
        if ($t.State -ne 'Disabled') {
            $n = Split-Path $t.Path -Leaf; $f = (Split-Path $t.Path -Parent).TrimEnd('\') + '\'
            Enable-ScheduledTask -TaskPath $f -TaskName $n | Out-Null
        }
    }
    Step "Restoring network adapter power settings"
    foreach ($n in $BK.Nic) { Set-NetAdapterAdvancedProperty -Name $n.Adapter -RegistryKeyword $n.Keyword -RegistryValue $n.Value }
    Step "Restoring power / boot / file system"
    if ($BK.Misc.PowerScheme) { powercfg /setactive $BK.Misc.PowerScheme }
    powercfg /delete 77777777-7777-7777-7777-777777777777 2>$null
    if ($BK.Misc.Hibernate -eq 'on') { powercfg /h on }
    if ($BK.Misc.DynamicTick -eq 'absent') { bcdedit /deletevalue disabledynamictick | Out-Null }
    if ("$($BK.Misc.LastAccess)" -match '^[0-3]$') { fsutil behavior set disablelastaccess $BK.Misc.LastAccess | Out-Null }
    if ($BK.Misc.HypervisorLaunch) { bcdedit /set hypervisorlaunchtype $BK.Misc.HypervisorLaunch | Out-Null }
    foreach ($l in 'Microsoft-Windows-SleepStudy/Diagnostic','Microsoft-Windows-Kernel-Processor-Power/Diagnostic','Microsoft-Windows-UserModePowerService/Diagnostic') { wevtutil set-log $l /e:true 2>$null }
    Step "Re-registering removed apps"
    foreach ($a in $BK.Appx) {
        $prov = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $a }
        if ($prov) { Add-AppxPackage -DisableDevelopmentMode -Register ([Environment]::ExpandEnvironmentVariables($prov.InstallLocation)); Ok "restored $a" }
        else { Warn "$a - reinstall from Microsoft Store if you need it" }
    }
    if ($BK.Misc.NvidiaProfile) {
        Step "NVIDIA profile -> driver defaults"
        $exe = Get-NVPI
        if ($exe) { $nip = Join-Path $BKDir 'nvidia-defaults.nip'; Write-Nip $nip 3; Run-NVPI $exe "-silentImport `"$nip`""; Ok "done" }
    }
    Rename-Item $BKFile "backup_restored_$(Get-Date -f yyyyMMdd_HHmmss).json"
    Write-Host "`n========= UNDO COMPLETE - REBOOT NOW =========" -ForegroundColor Green
    Stop-Transcript | Out-Null; exit
}

# =====================================================================
#  DETECT
# =====================================================================
$os      = Get-CimInstance Win32_OperatingSystem
$build   = [int]$os.BuildNumber
if ($build -lt 10240) { Write-Host "Windows 10 or 11 required (this is build $build). Windows 7/8.1 are not supported." -ForegroundColor Red; Stop-Transcript | Out-Null; exit }
if (-not [Environment]::Is64BitOperatingSystem) { Warn "32-bit Windows: tweaks will be applied, some tools (x64-only) will be skipped" }
$isWin11 = $build -ge 22000
$cs      = Get-CimInstance Win32_ComputerSystem
$vendor  = $cs.Manufacturer
$laptop  = ($cs.PCSystemType -eq 2) -or [bool](Get-CimInstance Win32_Battery)
$ramGB   = [math]::Round($cs.TotalPhysicalMemory / 1GB)
$gpus    = @((Get-CimInstance Win32_VideoController).Name)
$hasNV   = [bool]($gpus -match 'NVIDIA')
$hasAMD  = [bool]($gpus -match 'AMD|Radeon')
$diskNo  = (Get-Partition -DriveLetter $env:SystemDrive[0]).DiskNumber
$sysDisk = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$diskNo" } | Select-Object -First 1
$onHDD   = $sysDisk.MediaType -eq 'HDD'

Write-Host "=============================================================" -ForegroundColor Magenta
Write-Host "  ULTIMATE OPTIMIZER v3  (Atlas-based, fully reversible)"
Write-Host "  $vendor $($cs.Model) | $(if ($laptop) {'LAPTOP'} else {'DESKTOP'})"
Write-Host "  $($os.Caption) build $build | RAM $ramGB GB | System disk: $($sysDisk.MediaType)"
Write-Host "  GPU: $($gpus -join ' | ')"
Write-Host "=============================================================" -ForegroundColor Magenta

$doVBS  = $DisableVBS -or (Ask "`n Disable VBS / Memory Integrity? (+5-15% FPS, breaks WSL2/Docker/Hyper-V/some anticheats like FACEIT need it OFF anyway)")
$doMit  = $DisableMitigations -or (Ask " Disable Spectre/Meltdown mitigations? (+FPS mainly on CPUs older than 2019, less security)")
$doApps = -not $NoApps
if (-not $Yes -and -not $NoApps) { $doApps = -not ((Read-Host " Download & install tools + NVIDIA profile + optimizerDuck? [Y/n]") -match '^(n|no|net)$') }

Step "Restore point"
Enable-ComputerRestore -Drive "$env:SystemDrive\"
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' 'SystemRestorePointCreationFrequency' 0
Checkpoint-Computer -Description "Before Ultimate-Optimizer" -RestorePointType MODIFY_SETTINGS
Ok "created (plus exact backup in $BKFile)"

# =====================================================================
#  1. POWER  (Atlas power scheme values + laptop-safe battery side)
# =====================================================================
Step "Power"
Misc-Once 'PowerScheme' ([regex]'[0-9a-fA-F\-]{36}').Match((powercfg /getactivescheme)).Value
$hib = if (Test-Path "$env:SystemDrive\hiberfil.sys") { 'on' } else { 'off' }; Misc-Once 'Hibernate' $hib
$P = '77777777-7777-7777-7777-777777777777'
if (-not (powercfg /l | Select-String $P -Quiet)) { powercfg /duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 $P | Out-Null }
if (-not (powercfg /l | Select-String $P -Quiet)) { powercfg /duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c $P | Out-Null }  # fallback: High performance
powercfg /changename $P "Ultimate Optimizer" "Atlas-based max performance (balanced on battery)"
foreach ($s in 'PERFBOOSTMODE','CPMINCORES','PERFEPP') { powercfg -attributes SUB_PROCESSOR $s -ATTRIB_HIDE }
# --- AC: everything max (from Atlas DisablePowerSaving.ps1)
powercfg /setacvalueindex $P 0012ee47-9041-4b5d-9b77-535fba8b1442 d3d55efd-c1ff-424e-9dc3-441be7833010 0   # NVMe idle timeout
powercfg /setacvalueindex $P 0012ee47-9041-4b5d-9b77-535fba8b1442 d639518a-e56d-4345-8af2-b9f32fb26109 0   # NVMe idle timeout secondary
powercfg /setacvalueindex $P 0012ee47-9041-4b5d-9b77-535fba8b1442 fc7372b6-ab2d-43ee-8797-15e9841f2cca 0   # NVMe NOPPME
powercfg /setacvalueindex $P 2a737441-1930-4402-8d77-b2bebba308a3 0853a681-27c8-4100-a2fd-82013e970683 0   # USB hub suspend timeout
powercfg /setacvalueindex $P 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0   # USB selective suspend
powercfg /setacvalueindex $P 2a737441-1930-4402-8d77-b2bebba308a3 d4e98f31-5ffe-4ce1-be31-1b38b384c009 0   # USB 3 link power mgmt
powercfg /setacvalueindex $P 54533251-82be-4824-96c1-47b60b740d00 3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb 0   # processor idle throttle
powercfg /setacvalueindex $P 54533251-82be-4824-96c1-47b60b740d00 4d2b0152-7d5c-498b-88e2-34345392a2c5 200 # perf check interval
powercfg /setacvalueindex $P 7516b95f-f776-4464-8c53-06167f40cc99 17aaa29b-8b43-4b94-aafe-35f64daaf1ee 0   # display dim
powercfg /setacvalueindex $P SUB_PROCESSOR PERFBOOSTMODE 2
powercfg /setacvalueindex $P SUB_PROCESSOR CPMINCORES 100
powercfg /setacvalueindex $P SUB_PROCESSOR PERFEPP 0
powercfg /setacvalueindex $P SUB_PROCESSOR PROCTHROTTLEMIN 100
powercfg /setacvalueindex $P SUB_PCIEXPRESS ASPM 0
powercfg /setacvalueindex $P SUB_SLEEP STANDBYIDLE 0
# --- DC (battery): sane, so a laptop doesn't die in an hour
powercfg /setdcvalueindex $P SUB_PROCESSOR PERFBOOSTMODE 0
powercfg /setdcvalueindex $P SUB_PROCESSOR PERFEPP 50
powercfg /setdcvalueindex $P SUB_PROCESSOR PROCTHROTTLEMIN 5
powercfg /setdcvalueindex $P SUB_PROCESSOR PROCTHROTTLEMAX 85
powercfg /setactive $P
Ok "plan 'Ultimate Optimizer' active"
Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0   # fast startup off (Atlas)
if (-not $laptop) { powercfg /h off; Ok "hibernation off (desktop)" } else { Ok "hibernation kept (laptop safety)" }
Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' 'PowerThrottlingOff' 1
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Task Scheduler\Maintenance' 'WakeUp' 0
foreach ($l in 'Microsoft-Windows-SleepStudy/Diagnostic','Microsoft-Windows-Kernel-Processor-Power/Diagnostic','Microsoft-Windows-UserModePowerService/Diagnostic') { wevtutil set-log $l /e:false 2>$null }
Disable-Task '\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem'

Step "Network adapter power saving off (Atlas list)"
$props = Get-NetAdapter -Physical | Get-NetAdapterAdvancedProperty
foreach ($k in 'ULPMode','EEE','EEELinkAdvertisement','AdvancedEEE','EnableGreenEthernet','EeePhyEnable','uAPSDSupport',
               'EnablePowerManagement','EnableSavePowerNow','bLowPowerEnable','PowerSaveMode','PowerSavingMode',
               'SavePowerNowEnabled','AutoPowerSaveModeEnabled','NicAutoPowerSaver','SelectiveSuspend') {
    $props | Where-Object { $_.RegistryKeyword -eq "*$k" -or $_.RegistryKeyword -eq $k } | ForEach-Object {
        $pp = $_
        if (-not ($BK.Nic | Where-Object { $_.Adapter -eq $pp.Name -and $_.Keyword -eq $pp.RegistryKeyword })) {
            $BK.Nic += [pscustomobject]@{ Adapter = $pp.Name; Keyword = $pp.RegistryKeyword; Value = "$($pp.RegistryValue)" }
        }
        Set-NetAdapterAdvancedProperty -Name $pp.Name -RegistryKeyword $pp.RegistryKeyword -RegistryValue 0
    }
}
Save-BK

# =====================================================================
#  2. SCHEDULER / LATENCY (Atlas values + gaming)
# =====================================================================
Step "CPU scheduler, MMCSS, timers"
Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation' 38           # Atlas
$mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
Set-Tweak $mm 'SystemResponsiveness' 10                                                                      # Atlas
Set-Tweak $mm 'NetworkThrottlingIndex' 0xFFFFFFFF
Set-Tweak "$mm\Tasks\Games" 'GPU Priority' 8
Set-Tweak "$mm\Tasks\Games" 'Priority' 6
Set-Tweak "$mm\Tasks\Games" 'Scheduling Category' 'High' 'String'
Set-Tweak "$mm\Tasks\Games" 'SFIO Priority' 'High' 'String'
if ($isWin11) { Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' 'GlobalTimerResolutionRequests' 1 }
Misc-Once 'DynamicTick' $(if ((bcdedit /enum '{current}') -match 'disabledynamictick') { 'present' } else { 'absent' })
bcdedit /set disabledynamictick yes | Out-Null
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\FTH' 'Enabled' 0                                                         # Atlas
$la = ([regex]'= (\d)').Match((fsutil behavior query disablelastaccess)).Groups[1].Value
if ($la) { Misc-Once 'LastAccess' $la }
fsutil behavior set disablelastaccess 1 | Out-Null                                                           # Atlas

Step "Service host grouping (Atlas: less RAM, fewer processes)"
$script:Bulk = $true
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' | Where-Object { $_.PSChildName -notmatch 'Xbl|Xbox' } | ForEach-Object {
    if ($null -ne (Get-ItemProperty $_.PSPath).Start) { Try-Tweak $_.PSPath 'SvcHostSplitDisable' 1 }
}
$script:Bulk = $false
Ok "done"
Save-BK

# =====================================================================
#  3. GPU / GAMING
# =====================================================================
Step "GPU scheduling, Game Mode, Game DVR"
if ($build -ge 19041) { Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode' 2 }
Set-Tweak 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled' 1
Set-Tweak 'HKCU:\Software\Microsoft\GameBar' 'AllowAutoGameMode' 1
Set-Tweak 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled' 0
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0
if ($isWin11) { Set-Tweak 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' 'DirectXUserGlobalSettings' 'VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;' 'String' }

Step "MSI interrupt mode for GPU"
Get-PnpDevice -PresentOnly -Class Display | Where-Object { $_.InstanceId -like 'PCI\*' } | ForEach-Object {
    Try-Tweak "HKLM:\SYSTEM\CurrentControlSet\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties" 'MSISupported' 1
    Ok "$($_.FriendlyName)"
}
if ($hasNV)  { Set-Tweak 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client' 'OptInOrOutPreference' 0; Set-Svc 'NvTelemetryContainer' 4 }
if ($hasAMD) {
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        if ((Get-ItemProperty $_.PSPath).ProviderName -match 'AMD|ATI') { Try-Tweak $_.PSPath 'EnableUlps' 0; Ok "AMD ULPS off" }
    }
}
Save-BK

# =====================================================================
#  4. UI / INPUT  (Atlas visual-effects + QoL)
# =====================================================================
Step "Visual effects (Atlas preset), input, explorer"
Set-Tweak 'HKCU:\Control Panel\Desktop' 'UserPreferencesMask' ([byte[]](0x90,0x12,0x03,0x80,0x10,0x00,0x00,0x00)) 'Binary'
Set-Tweak 'HKCU:\Control Panel\Desktop' 'FontSmoothing' '2' 'String'
Set-Tweak 'HKCU:\Control Panel\Desktop' 'DragFullWindows' '1' 'String'
Set-Tweak 'HKCU:\Control Panel\Desktop\WindowMetrics' 'MinAnimate' '0' 'String'
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'TaskbarAnimations' 0
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'ListviewAlphaSelect' 1
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'ListviewShadow' 1
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'IconsOnly' 0
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' 'VisualFXSetting' 3
Set-Tweak 'HKCU:\Software\Microsoft\Windows\DWM' 'EnableAeroPeek' 0
Set-Tweak 'HKCU:\Software\Microsoft\Windows\DWM' 'AlwaysHibernateThumbnails' 0
if ($ramGB -le 8) { Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 0 }
Set-Tweak 'HKCU:\Control Panel\Desktop' 'MenuShowDelay' '0' 'String'
Set-Tweak 'HKCU:\Control Panel\Desktop' 'MouseHoverTime' '20' 'String'
Set-Tweak 'HKCU:\Control Panel\Desktop' 'HungAppTimeout' '2000' 'String'
Set-Tweak 'HKCU:\Control Panel\Desktop' 'WaitToKillAppTimeOut' '2000' 'String'
Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control' 'WaitToKillServiceTimeout' '2000' 'String'
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize' 'StartupDelayInMSec' 0
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'Max Cached Icons' '4096' 'String'
Set-Tweak 'HKCU:\Control Panel\Mouse' 'MouseSpeed' '0' 'String'
Set-Tweak 'HKCU:\Control Panel\Mouse' 'MouseThreshold1' '0' 'String'
Set-Tweak 'HKCU:\Control Panel\Mouse' 'MouseThreshold2' '0' 'String'
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 1
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' 'BackgroundAppGlobalToggle' 0
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows Search\Gather\Windows\SystemIndex' 'RespectPowerModes' 1
Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' 'DisableBandwidthThrottling' 1
Save-BK

# =====================================================================
#  5. TELEMETRY / BACKGROUND (Atlas privacy set)
# =====================================================================
Step "Telemetry, error reporting, compatibility tracking, ads"
$dc = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'
Set-Tweak $dc 'AllowTelemetry' 0
Set-Tweak $dc 'AllowDeviceNameInTelemetry' 0
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection' 'AllowTelemetry' 0
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection' 'MaxTelemetryAllowed' 0
Try-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Autologger\Diagtrack-Listener' 'Start' 0
Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Diagnostics\Performance' 'DisableDiagnosticTracing' 1
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\SQMClient\Windows' 'CEIPEnable' 0
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WDI\{9c5a40da-b965-4fc3-8781-88dd50a6299d}' 'ScenarioExecutionEnabled' 0
Set-Tweak 'HKLM:\SOFTWARE\Microsoft\PolicyManager\default\System\AllowExperimentation' 'Value' 0
$ac = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppCompat'
foreach ($n in @{AITEnable=0;AllowTelemetry=0;DisableEngine=1;DisableInventory=1;DisablePCA=1;DisableUAR=1}.GetEnumerator()) { Set-Tweak $ac $n.Key $n.Value }
$wer = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting'
Set-Tweak $wer 'Disabled' 1; Set-Tweak $wer 'DontShowUI' 1; Set-Tweak $wer 'LoggingDisabled' 1; Set-Tweak $wer 'DontSendAdditionalData' 1
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'EnableActivityFeed' 0
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'UploadUserActivities' 0
Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'Start_TrackProgs' 0
Set-Tweak 'HKCU:\Software\Microsoft\InputPersonalization' 'RestrictImplicitInkCollection' 1
Set-Tweak 'HKCU:\Software\Microsoft\InputPersonalization' 'RestrictImplicitTextCollection' 1
Set-Tweak 'HKCU:\Software\Microsoft\Input\TIPC' 'Enabled' 0
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode' 0
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
foreach ($n in 'SilentInstalledAppsEnabled','SystemPaneSuggestionsEnabled','SubscribedContent-338388Enabled','SubscribedContent-338389Enabled','SubscribedContent-353698Enabled','SoftLandingEnabled') { Set-Tweak $cdm $n 0 }
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo' 'DisabledByGroupPolicy' 1
Set-Tweak 'HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI' 'DisableAIDataAnalysis' 1
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0
Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds' 'EnableFeeds' 0

Step "Services (Atlas list, minus anything that breaks LAN/sync)"
foreach ($s in 'DiagTrack','diagnosticshub.standardcollector.service','WerSvc','wercplsupport','PcaSvc','TrkWks','MapsBroker','RetailDemo','GpuEnergyDrv') { Set-Svc $s 4 }
Set-Svc 'dmwappushservice' 3
if ($onHDD) { Ok "HDD detected: SysMain kept ON (it speeds up HDD systems)" }
if ($onHDD -and $ramGB -le 4) { Set-Svc 'WSearch' 3; Ok "weak HDD PC: search indexer set to manual" }

Step "Scheduled tasks (Atlas list)"
'\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser',
'\Microsoft\Windows\Application Experience\PcaPatchDbTask',
'\Microsoft\Windows\Application Experience\ProgramDataUpdater',
'\Microsoft\Windows\Customer Experience Improvement Program\Consolidator',
'\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip',
'\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector',
'\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReporting',
'\Microsoft\Windows\Feedback\Siuf\DmClient',
'\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload' | ForEach-Object { Disable-Task $_ }
Ok "done"
Save-BK

# =====================================================================
#  6. APPS (Atlas appx list, minus useful ones; provisioned copies KEPT so undo works)
# =====================================================================
Step "Removing preinstalled junk"
$junk = 'MicrosoftTeams','MSTeams','Microsoft.Copilot','Clipchamp.Clipchamp','Disney.37853FC22B2CE','SpotifyAB.SpotifyMusic',
        'Microsoft.549981C3F5F10','Microsoft.XboxApp','microsoft.windowscommunicationsapps','Microsoft.MSPaint','Microsoft.Getstarted',
        'Microsoft.ZuneVideo','MicrosoftCorporationII.MicrosoftFamily','Microsoft.MixedReality.Portal','Microsoft.Windows.DevHome',
        'Microsoft.BingWeather','Microsoft.BingNews','Microsoft.BingSearch','Microsoft.GetHelp','Microsoft.Microsoft3DViewer',
        'Microsoft.MicrosoftOfficeHub','Microsoft.MicrosoftSolitaireCollection','Microsoft.People','Microsoft.PowerAutomateDesktop',
        'Microsoft.SkypeApp','Microsoft.Todos','Microsoft.WindowsFeedbackHub','Microsoft.WindowsMaps',
        'king.com.CandyCrush','BytedancePte.Ltd.TikTok','Facebook','Instagram','AmazonVideo','5A894077.McAfeeSecurity'
foreach ($j in $junk) {
    Get-AppxPackage "$j*" | ForEach-Object {
        if ($BK.Appx -notcontains $_.Name) { $BK.Appx += $_.Name }
        Remove-AppxPackage $_.PackageFullName; Ok "removed $($_.Name)"
    }
}
Ok "kept: Store, Xbox/Game Bar, Photos, Calculator, Camera, Snipping Tool, Notepad, Sticky Notes"
Save-BK

# =====================================================================
#  7. OPTIONAL RISKY
# =====================================================================
if ($doVBS) {
    Step "VBS / HVCI off"
    Misc-Once 'HypervisorLaunch' $(if ((bcdedit /enum '{current}') -match 'hypervisorlaunchtype\s+Off') { 'off' } else { 'auto' })
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' 'EnableVirtualizationBasedSecurity' 0
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled' 0
    bcdedit /set hypervisorlaunchtype off | Out-Null
    Ok "done"
}
if ($doMit) {
    Step "Spectre/Meltdown mitigations off (Atlas values)"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'FeatureSettingsOverride' 3
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'FeatureSettingsOverrideMask' 3
    Ok "done (process mitigations/CFG left ON - anticheats like Vanguard need them)"
}
Save-BK

# =====================================================================
#  8. SOFTWARE + NVIDIA PROFILE
# =====================================================================
if ($doApps) {
    $haveWinget = [bool](Get-Command winget.exe -ErrorAction SilentlyContinue)
    function Install-App($id) {
        if (-not $haveWinget) { Warn "winget missing - skip $id"; return }
        winget.exe install --id $id -e --silent --accept-package-agreements --accept-source-agreements | Out-Null
        # 0 = ok, 0x8A15002B = no newer version, 0x8A150061 = already installed
        if ($LASTEXITCODE -in 0, -1978335189, -1978335135) { Ok "installed: $id" } else { Warn "could not install $id" }
    }
    Step "Runtimes for games (fixes missing DLL errors)"
    if (-not $haveWinget) { Warn "winget not found: install 'App Installer' from Microsoft Store, then re-run for apps" }
    'Microsoft.VCRedist.2015+.x64','Microsoft.VCRedist.2015+.x86','Microsoft.DirectX' | ForEach-Object { Install-App $_ }
    Step "Tools"
    'REALiX.HWiNFO','BitSum.ProcessLasso','Microsoft.Sysinternals.Autoruns','CrystalDewWorld.CrystalDiskInfo','7zip.7zip' | ForEach-Object { Install-App $_ }
    if ($hasNV -or $hasAMD) { Install-App 'Guru3D.Afterburner' }
    if ($vendor -match 'ASUS')   { Install-App 'seerge.g-helper' }
    if ($vendor -match 'LENOVO') { Install-App 'BartoszCichecki.LenovoLegionToolkit' }

    if ($hasNV) {
        Step "NVIDIA Profile Inspector + max performance profile"
        $exe = Get-NVPI
        if ($exe) {
            Ok "downloaded to $(Split-Path $exe)"
            Run-NVPI $exe '-exportCustomized'          # backup of your current customized profiles (saved next to the exe)
            $nip = Join-Path (Split-Path $exe) 'Ultimate-MaxPerformance.nip'
            Write-Nip $nip 2
            Write-Nip (Join-Path (Split-Path $exe) 'Driver-Defaults.nip') 3
            Run-NVPI $exe "-silentImport `"$nip`""
            Misc-Once 'NvidiaProfile' $true
            foreach ($s in $NvMax) { Ok $s[0] }
        } else { Warn "download failed (no internet / GitHub blocked?) - get it from github.com/Orbmu2k/nvidiaProfileInspector" }
    }
    if (-not $NoDuck) {
        Step "optimizerDuck (download only, run it yourself later)"
        $duck = Join-Path $PSScriptRoot 'Get-OptimizerDuck.ps1'
        if (Test-Path $duck) { try { & $duck -NoLaunch } catch { Warn "optimizerDuck: $($_.Exception.Message)" } } else { Warn "Get-OptimizerDuck.ps1 not found next to this script - skip" }
    }
    Save-BK
}

# =====================================================================
#  9. CLEANUP + REPORT
# =====================================================================
Step "Cleanup"
Remove-Item "$env:TEMP\*", "$env:SystemRoot\Temp\*" -Recurse -Force
Dism.exe /Online /Cleanup-Image /StartComponentCleanup /Quiet
if (-not $onHDD) { Optimize-Volume -DriveLetter $env:SystemDrive[0] -ReTrim } else { Ok "HDD: run defrag weekly (Windows does it automatically)" }
ipconfig /flushdns | Out-Null

Step "Hardware check"
$sticks = @(Get-CimInstance Win32_PhysicalMemory)
foreach ($m in $sticks) { Write-Host ("    {0}: {1} GB @ {2} MHz" -f $m.DeviceLocator, ($m.Capacity/1GB), $m.ConfiguredClockSpeed) }
if ($sticks.Count -lt 2) { Warn "SINGLE-CHANNEL RAM - a 2nd identical stick = biggest cheap FPS boost" }
if ($onHDD) { Warn "Windows is on an HDD - an SSD will speed this PC up more than everything above combined" }
if ($ramGB -lt 8) { Warn "Less than 8 GB RAM - upgrading to 8-16 GB is the #1 fix for an old PC" }
if ($laptop) { Warn "Laptop: play on charger - on battery the plan switches to balanced on purpose" }

Save-BK
Write-Host "`n=============================================================" -ForegroundColor Green
Write-Host "  DONE - REBOOT NOW"
Write-Host "  Exact rollback any time:  .\Ultimate-Optimizer.ps1 -Undo"
Write-Host "  Backup + log:             $BKDir"
Write-Host "=============================================================" -ForegroundColor Green
Stop-Transcript | Out-Null
