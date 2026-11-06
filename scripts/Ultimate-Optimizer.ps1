<#
=====================================================================
  Ultimate-Optimizer.ps1  v3
  Atlas-level tuning for ANY Windows 10 / 11 PC or laptop (x64), no reinstall.

  - Tweaks taken from AtlasOS Playbook v0.5.0 (only the ones that can't break things)
    + gaming tweaks (MMCSS, GPU priority, HAGS, MSI mode, Game DVR, power plan)
  - Hardware-aware: laptop/desktop, SSD/HDD, RAM size, NVIDIA/AMD/Intel, vendor
  - Downloads tools (winget) + NVIDIA Profile Inspector (GitHub) and imports
    a max-performance driver profile (setting IDs verified against NVIDIA NVAPI header)
  - Extra tweaks ported from optimizerDuck (GPU vendor power states, input, privacy, services)
  - EVERY change is recorded first -> exact rollback:  .\Ultimate-Optimizer.ps1 -Undo

  Usage (PowerShell as Administrator):
     Set-ExecutionPolicy -Scope Process Bypass -Force
     .\Ultimate-Optimizer.ps1            interactive (asks about risky stuff)
     .\Ultimate-Optimizer.ps1 -Yes       no questions, safe defaults, installs tools
     .\Ultimate-Optimizer.ps1 -NoApps    skip software downloads
     .\Ultimate-Optimizer.ps1 -Undo      restore EVERYTHING exactly as it was
     -DisableVBS / -DisableMitigations   turn the risky options on without asking (used by the GUI)
     -Only power.plan,cpu.mmcss,...      apply exactly these tweaks (ids: launcher/ui/tweaks.json)
     -NoRestorePoint                     skip the System Restore point (used for the quick cleanup)
     -SetDns 1.1.1.1,1.0.0.1 | auto      set DNS on active adapters (backed up, undone by -Undo)
     -RemoveApps Name1,Name2             remove these Store apps (backed up like the junk list)
=====================================================================
#>
# -NoDuck is accepted for compatibility with older launchers and ignored (optimizerDuck is no longer downloaded)
param([switch]$Undo, [switch]$Yes, [switch]$NoApps, [switch]$NoDuck, [switch]$DisableVBS, [switch]$DisableMitigations, [string]$Only, [switch]$NoRestorePoint, [string]$SetDns, [string]$RemoveApps)

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
# Remove-AppxPackage can block forever (busy deployment queue: Store updating apps, app in use).
# Run it in a job with a time limit so the optimizer never stalls on one package.
function Remove-AppxTimed($pkg, $limit = 90) {
    Write-Host "    removing $($pkg.Name)..."
    $job = Start-Job -ScriptBlock { param($full) Remove-AppxPackage -Package $full -ErrorAction Stop } -ArgumentList $pkg.PackageFullName
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Wait-Job $job -Timeout 15)) {
        if ($sw.Elapsed.TotalSeconds -ge $limit) {
            Stop-Job $job; Remove-Job $job -Force
            Warn "skip $($pkg.Name): Windows did not answer in $limit s (Store busy updating apps?) - remove it later in Tools -> Apps"
            return $false
        }
        Write-Host ("    ... still removing $($pkg.Name) ({0} s)" -f [int]$sw.Elapsed.TotalSeconds)
    }
    $err = $job.ChildJobs[0].Error | Select-Object -First 1
    Remove-Job $job -Force
    if (Get-AppxPackage -Name $pkg.Name | Where-Object PackageFullName -eq $pkg.PackageFullName) {
        Warn "could not remove $($pkg.Name)$(if ($err) { ': ' + $err })"
        return $false
    }
    Ok "removed $($pkg.Name)"
    return $true
}
# Runs a slow console tool hidden, prints a heartbeat every 30 s and stops it after $limit seconds.
function Run-Long($label, $exe, $argLine, $limit) {
    Write-Host "    $label - may take several minutes..."
    try { $pr = Start-Process -FilePath $exe -ArgumentList $argLine -WindowStyle Hidden -PassThru -ErrorAction Stop }
    catch { Warn "$label could not start: $($_.Exception.Message)"; return $false }
    $null = $pr.Handle    # keeps ExitCode readable after the process ends
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $pr.WaitForExit(30000)) {
        if ($sw.Elapsed.TotalSeconds -ge $limit) {
            try { $pr.Kill() } catch {}
            Warn ("$label stopped after {0} min - Windows finishes it on its own during idle maintenance" -f [int]($limit / 60))
            return $false
        }
        Write-Host ("    ... {0} still working ({1}:{2:00})" -f $label, [int]$sw.Elapsed.TotalMinutes, $sw.Elapsed.Seconds)
    }
    if ($pr.ExitCode -ne 0) { Warn "$label exit code $($pr.ExitCode)"; return $false }
    return $true
}
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
    # sc.exe stop only sends the request; Stop-Service waits forever for drivers/services that cannot stop
    # (e.g. GpuEnergyDrv). Disabled takes full effect after the reboot anyway.
    if ($Start -eq 4) { & sc.exe stop $Name 2>&1 | Out-Null }
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
# Older versions set SvcHostSplitDisable=1 on every service (Atlas). On Windows 11 24H2+ that can stop
# Explorer from starting, so any later run puts the original values back first.
if (-not $Undo) {
    $oldSplit = @($BK.Reg | Where-Object { $_.Name -eq 'SvcHostSplitDisable' })
    if ($oldSplit.Count) {
        Step "Reverting per-service svchost split change from an older version ($($oldSplit.Count) services)"
        foreach ($r in $oldSplit) {
            if ($r.Exists) { Set-ItemProperty -Path $r.Path -Name $r.Name -Value $r.Value -Force }
            else { Remove-ItemProperty -Path $r.Path -Name $r.Name -Force }
        }
        $BK.Reg = @($BK.Reg | Where-Object { $_.Name -ne 'SvcHostSplitDisable' })
        Save-BK
        Ok "done"
    }
}

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
    if ($BK.Misc.UsbPower) {
        Step "Restoring USB power management"
        $cur = @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable)
        foreach ($u in @($BK.Misc.UsbPower)) {
            $c = $cur | Where-Object { $_.InstanceName -eq $u.Instance } | Select-Object -First 1
            if ($c) { Set-CimInstance -InputObject $c -Property @{ Enable = [bool]$u.Enable } }
        }
    }
    if ($BK.Misc.TcpAutoTuning) { Set-NetTCPSetting -SettingName Internet -AutoTuningLevelLocal $BK.Misc.TcpAutoTuning }
    if ($BK.Misc.Rss) { Set-NetOffloadGlobalSetting -ReceiveSideScaling $BK.Misc.Rss }
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
        if ($prov) {
            Write-Host "    restoring $a..."
            $job = Start-Job -ScriptBlock { param($m) Add-AppxPackage -DisableDevelopmentMode -Register $m -ErrorAction Stop } -ArgumentList ([Environment]::ExpandEnvironmentVariables($prov.InstallLocation))
            if (Wait-Job $job -Timeout 120) { Ok "restored $a" } else { Stop-Job $job; Warn "restore of $a timed out - install it from Microsoft Store" }
            Remove-Job $job -Force
        }
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

# ---------------------------------------------------------------------
#  QUICK MODES (used by the app): change DNS / remove selected apps, then exit
# ---------------------------------------------------------------------
if ($SetDns -or $RemoveApps) {
    if ($SetDns) {
        Step "DNS: $SetDns"
        $servers = @($SetDns -split '[,;\s]+' | Where-Object { $_ -and $_ -ne 'auto' })
        foreach ($a in @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up')) {
            $key = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($a.InterfaceGuid)"
            if ($servers.Count) {
                Set-Tweak $key 'NameServer' ($servers -join ',') 'String'          # records the original (or DHCP) first
                Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses $servers
            } else {
                Set-Tweak $key 'NameServer' '' 'String'
                Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses
            }
            Ok "$($a.Name): $(if ($servers.Count) { $servers -join ', ' } else { 'automatic (from router / provider)' })"
        }
        Clear-DnsClientCache
    }
    if ($RemoveApps) {
        Step "Removing selected apps"
        foreach ($name in @($RemoveApps -split '[,;\s]+' | Where-Object { $_ })) {
            Get-AppxPackage -Name $name | ForEach-Object {
                if ($BK.Appx -notcontains $_.Name) { $BK.Appx += $_.Name }
                $null = Remove-AppxTimed $_
            }
        }
    }
    Save-BK
    Write-Host "DONE"
    Stop-Transcript | Out-Null
    exit
}

# ---------------------------------------------------------------------
#  WHAT TO APPLY: every tweak has an id (catalog: launcher/ui/tweaks.json).
#  -Only a,b,c applies exactly that list (used by the app's profiles / switches).
#  Without -Only: everything except the risky ones; software depends on -NoApps.
# ---------------------------------------------------------------------
$doVBS  = $DisableVBS -or (-not $Only -and (Ask "`n Disable VBS / Memory Integrity? (+5-15% FPS, breaks WSL2/Docker/Hyper-V/some anticheats like FACEIT need it OFF anyway)"))
$doMit  = $DisableMitigations -or (-not $Only -and (Ask " Disable Spectre/Meltdown mitigations? (+FPS mainly on CPUs older than 2019, less security)"))
$doApps = -not $NoApps
if (-not $Yes -and -not $NoApps -and -not $Only) { $doApps = -not ((Read-Host " Download & install tools + NVIDIA profile? [Y/n]") -match '^(n|no|net)$') }
$Sel = @($Only -split '[,;\s]+' | Where-Object { $_ })
function Use($id) {
    if ($Sel.Count) { return $Sel -contains $id }
    switch -Wildcard ($id) {
        'risk.vbs'         { return $doVBS }
        'risk.mitigations' { return $doMit }
        'soft.*'           { return $doApps }
        'ui.transparency'  { return $ramGB -le 8 }
        default            { return $true }
    }
}
if ($Sel.Count) { Ok "selected: $($Sel.Count) tweaks" }

if (-not $NoRestorePoint) {
    Step "Restore point"
    Enable-ComputerRestore -Drive "$env:SystemDrive\"
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' 'SystemRestorePointCreationFrequency' 0
    Checkpoint-Computer -Description "Before Ultimate-Optimizer" -RestorePointType MODIFY_SETTINGS
    Ok "created (plus exact backup in $BKFile)"
}

# =====================================================================
#  1. POWER  (Atlas power scheme values + laptop-safe battery side)
# =====================================================================
if (Use 'power.plan') {
    Step "Power plan"
    Misc-Once 'PowerScheme' ([regex]'[0-9a-fA-F\-]{36}').Match((powercfg /getactivescheme)).Value
    $P = '77777777-7777-7777-7777-777777777777'
    if (-not (powercfg /l | Select-String $P -Quiet)) { powercfg /duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 $P | Out-Null }
    if (-not (powercfg /l | Select-String $P -Quiet)) { powercfg /duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c $P | Out-Null }  # fallback: High performance
    powercfg /changename $P "Ultimate Optimizer" "Atlas-based max performance (balanced on battery)"
    foreach ($s in 'PERFBOOSTMODE','CPMINCORES','PERFEPP') { powercfg -attributes SUB_PROCESSOR $s -ATTRIB_HIDE }
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
}
if (Use 'power.faststartup') {
    Step "Fast startup off"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0   # Atlas
    Ok "done"
}
if (Use 'power.hibernate') {
    $hib = if (Test-Path "$env:SystemDrive\hiberfil.sys") { 'on' } else { 'off' }; Misc-Once 'Hibernate' $hib
    if (-not $laptop) { powercfg /h off; Ok "hibernation off (desktop, frees disk space)" } else { Ok "hibernation kept (laptop safety)" }
}
if (Use 'power.throttling') {
    Step "Power throttling off"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' 'PowerThrottlingOff' 1
    Ok "done"
}
if (Use 'power.diag') {
    Step "Power diagnostics and maintenance wake-ups off"
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Task Scheduler\Maintenance' 'WakeUp' 0
    foreach ($l in 'Microsoft-Windows-SleepStudy/Diagnostic','Microsoft-Windows-Kernel-Processor-Power/Diagnostic','Microsoft-Windows-UserModePowerService/Diagnostic') { wevtutil set-log $l /e:false 2>$null }
    Disable-Task '\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem'
    Ok "done"
}
if (Use 'power.usb') {
    Step "USB devices: no power saving"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\USB\AutomaticSurpriseRemoval' 'AttemptRecoveryFromUsbPowerDrain' 0
    $usb = @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable | Where-Object { $_.InstanceName -match '^USB' })
    if (-not $BK.Misc.ContainsKey('UsbPower')) { Misc-Once 'UsbPower' @($usb | ForEach-Object { [pscustomobject]@{ Instance = $_.InstanceName; Enable = [bool]$_.Enable } }) }
    $n = 0
    foreach ($u in $usb) { if ($u.Enable) { Set-CimInstance -InputObject $u -Property @{ Enable = $false }; $n++ } }
    Ok "$n USB device(s) won't be turned off to save power"
}
if (Use 'power.nic') {
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
    Ok "done"
}
Save-BK

# ---------------------------------------------------------------------
#  NETWORK LATENCY - adapter properties are recorded in $BK.Nic and restored by -Undo
# ---------------------------------------------------------------------
function NicSet($keywords, [string]$value, [switch]$Lowest) {
    $n = 0
    foreach ($pp in @(Get-NetAdapter -Physical | Where-Object Status -ne 'Disabled' | Get-NetAdapterAdvancedProperty)) {
        if ($keywords -notcontains $pp.RegistryKeyword) { continue }
        $valid = @($pp.ValidRegistryValues)
        $target = $value
        if ($Lowest) { if (-not $valid.Count) { continue }; $target = "$(@($valid | ForEach-Object { [int]$_ } | Sort-Object)[0])" }
        if ($valid.Count -and $valid -notcontains $target) { continue }
        if ("$($pp.RegistryValue)" -eq $target) { continue }
        if (-not ($BK.Nic | Where-Object { $_.Adapter -eq $pp.Name -and $_.Keyword -eq $pp.RegistryKeyword })) {
            $BK.Nic += [pscustomobject]@{ Adapter = $pp.Name; Keyword = $pp.RegistryKeyword; Value = "$($pp.RegistryValue)" }
        }
        Set-NetAdapterAdvancedProperty -Name $pp.Name -RegistryKeyword $pp.RegistryKeyword -RegistryValue $target
        Ok "$($pp.Name): $($pp.DisplayName) -> $target"; $n++
    }
    return $n
}
if (Use 'net.nagle') {
    Step "Network: Nagle's algorithm off (packets are sent immediately)"
    $n = 0
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces' | ForEach-Object {
        $ip = Get-ItemProperty $_.PSPath
        if ($ip.DhcpIPAddress -or ($ip.IPAddress -and "$($ip.IPAddress)" -ne '0.0.0.0')) {
            Set-Tweak $_.PSPath 'TcpAckFrequency' 1; Set-Tweak $_.PSPath 'TCPNoDelay' 1; Set-Tweak $_.PSPath 'TcpDelAckTicks' 0; $n++
        }
    }
    Ok "$n network interface(s): no delayed ACK, no Nagle"
}
if (Use 'net.nic.latency') {
    Step "Network adapter: low latency mode"
    [void](NicSet @('*InterruptModeration') '0')
    [void](NicSet @('ITR') '0')
    [void](NicSet @('*FlowControl') '0')
    [void](NicSet @('*LsoV2IPv4','*LsoV2IPv6','*LsoV1IPv4') '0')
    [void](NicSet @('*PacketCoalescing') '0')
    Ok "done (interrupt moderation, flow control, large send offload, packet coalescing off)"
}
if (Use 'net.wifi') {
    Step "Wi-Fi: no background roaming scans (fewer ping spikes)"
    $n = NicSet @('RoamAggressiveness','RoamingAggressiveness','*RoamAggressiveness','RoamTrigger') '' -Lowest
    [void](NicSet @('MIMOPowerSaveMode') '3')
    Ok "$n Wi-Fi setting(s) changed"
}
if (Use 'net.tcp') {
    Step "TCP stack: receive window auto-tuning, RSS, ports"
    $at = (Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal
    if ($at) { Misc-Once 'TcpAutoTuning' "$at" }
    Set-NetTCPSetting -SettingName Internet -AutoTuningLevelLocal Normal
    $rss = (Get-NetOffloadGlobalSetting).ReceiveSideScaling
    if ($rss) { Misc-Once 'Rss' "$rss" }
    Set-NetOffloadGlobalSetting -ReceiveSideScaling Enabled
    $tp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters'
    Set-Tweak $tp 'MaxUserPort' 65534
    Set-Tweak $tp 'TcpTimedWaitDelay' 30
    Set-Tweak $tp 'DefaultTTL' 64
    Ok "auto-tuning normal, RSS on, more ephemeral ports, faster port reuse"
}
if (Use 'net.qos') {
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit' 0
    Ok "QoS: no bandwidth reserved for Windows"
}
Save-BK

# =====================================================================
#  2. SCHEDULER / LATENCY (Atlas values + gaming)
# =====================================================================
if (Use 'cpu.priority') {
    Step "CPU scheduler: foreground priority"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation' 38           # Atlas
    Ok "Win32PrioritySeparation = 38"
}
$mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
if (Use 'cpu.mmcss') {
    Step "MMCSS: games get priority"
    Set-Tweak $mm 'SystemResponsiveness' 10                                                                      # Atlas
    Set-Tweak "$mm\Tasks\Games" 'GPU Priority' 8
    Set-Tweak "$mm\Tasks\Games" 'Priority' 6
    Set-Tweak "$mm\Tasks\Games" 'Scheduling Category' 'High' 'String'
    Set-Tweak "$mm\Tasks\Games" 'SFIO Priority' 'High' 'String'
    Ok "done"
}
if (Use 'cpu.mmcss.nolazy') {
    Set-Tweak $mm 'NoLazyMode' 1
    Set-Tweak $mm 'AlwaysOn' 1
    Ok "MMCSS: no lazy mode (lowest audio/game latency, a bit more power)"
}
if (Use 'cpu.timer') {
    Step "Timers: dynamic tick off, global timer resolution"
    if ($isWin11) { Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' 'GlobalTimerResolutionRequests' 1 }
    Misc-Once 'DynamicTick' $(if ((bcdedit /enum '{current}') -match 'disabledynamictick') { 'present' } else { 'absent' })
    bcdedit /set disabledynamictick yes | Out-Null
    Ok "done"
}
if (Use 'cpu.fth') {
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\FTH' 'Enabled' 0                                                     # Atlas
    Ok "Fault Tolerant Heap off"
}
if (Use 'fs.lastaccess') {
    $la = ([regex]'= (\d)').Match((fsutil behavior query disablelastaccess)).Groups[1].Value
    if ($la) { Misc-Once 'LastAccess' $la }
    fsutil behavior set disablelastaccess 1 | Out-Null                                                       # Atlas
    Ok "NTFS last access time off"
}
if (Use 'svc.split') {
    Step "Service host grouping (Atlas: less RAM, fewer processes)"
    # The supported knob: Windows groups services itself when RAM is below this threshold (as on 4 GB PCs).
    # The old per-service SvcHostSplitDisable=1 (Atlas) broke Explorer on Windows 11 24H2+ and is no longer used.
    $kb = [int64]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1KB) + 1024
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control' 'SvcHostSplitThresholdInKB' ([int]$kb)
    Ok "svchost grouping via SvcHostSplitThresholdInKB (Windows' own low-RAM mode)"
}
if (Use 'net.throttle') {
    Step "Network throttling off"
    Set-Tweak $mm 'NetworkThrottlingIndex' 0xFFFFFFFF
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' 'DisableBandwidthThrottling' 1
    Ok "done"
}
Save-BK

# =====================================================================
#  3. GPU / GAMING
# =====================================================================
if ((Use 'gpu.hags') -and $build -ge 19041) {
    Step "Hardware-accelerated GPU scheduling"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode' 2
    Ok "on (after reboot)"
}
if (Use 'gpu.gamemode') {
    Set-Tweak 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled' 1
    Set-Tweak 'HKCU:\Software\Microsoft\GameBar' 'AllowAutoGameMode' 1
    Ok "Game Mode on"
}
if (Use 'gpu.gamedvr') {
    Step "Game DVR (background recording) off"
    Set-Tweak 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled' 0
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0
    Ok "done"
}
if ((Use 'gpu.flipmodel') -and $isWin11) {
    Set-Tweak 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' 'DirectXUserGlobalSettings' 'VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;' 'String'
    Ok "windowed games: flip model + VRR optimizations"
}
if (Use 'gpu.msi') {
    Step "MSI interrupt mode for GPU"
    Get-PnpDevice -PresentOnly -Class Display | Where-Object { $_.InstanceId -like 'PCI\*' } | ForEach-Object {
        Try-Tweak "HKLM:\SYSTEM\CurrentControlSet\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties" 'MSISupported' 1
        Ok "$($_.FriendlyName)"
    }
}
if (Use 'gpu.vendor') {
    Step "GPU vendor tweaks"
    if ($hasNV)  { Set-Tweak 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client' 'OptInOrOutPreference' 0; Set-Svc 'NvTelemetryContainer' 4 }
    if ($hasAMD) {
        Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
            if ((Get-ItemProperty $_.PSPath).ProviderName -match 'AMD|ATI') { Try-Tweak $_.PSPath 'EnableUlps' 0; Ok "AMD ULPS off" }
        }
    }
}
# per-vendor driver keys under the display adapter class
$gpuKeys = @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' |
             Where-Object { $_.PSChildName -match '^\d{4}$' } |
             ForEach-Object { $ip = Get-ItemProperty $_.PSPath; [pscustomobject]@{ Path = $_.PSPath; Vendor = "$($ip.ProviderName) $($ip.DriverDesc)" } })
function GpuSet($vendorRx, $name, $value) { foreach ($g in $gpuKeys | Where-Object { $_.Vendor -match $vendorRx }) { Try-Tweak $g.Path $name $value } }
if ((Use 'gpu.amd.aspm') -and $hasAMD) { GpuSet 'AMD|ATI' 'EnableAspmL0s' 0; GpuSet 'AMD|ATI' 'EnableAspmL1' 0; Ok "AMD: PCIe ASPM off" }
if ((Use 'gpu.amd.powergating') -and $hasAMD) {
    GpuSet 'AMD|ATI' 'DisablePowerGating' 1; GpuSet 'AMD|ATI' 'PP_GPUPowerDownEnabled' 0; GpuSet 'AMD|ATI' 'DisableDynamicPstate' 1
    Ok "AMD: power gating off"
}
if ((Use 'gpu.amd.clockgating') -and $hasAMD) {
    GpuSet 'AMD|ATI' 'DisableVCEPowerGating' 1; GpuSet 'AMD|ATI' 'DisableVceClockGating' 1
    GpuSet 'AMD|ATI' 'EnableUvdClockGating' 0; GpuSet 'AMD|ATI' 'EnableVceSwClockGating' 0
    Ok "AMD: video engine clock gating off"
}
if ((Use 'gpu.nv.pstate') -and $hasNV) { GpuSet 'NVIDIA' 'DisableDynamicPstate' 1; Ok "NVIDIA: dynamic P-states off (GPU stays at full clocks)" }
if ((Use 'gpu.nv.asyncpstates') -and $hasNV) { GpuSet 'NVIDIA' 'DisableASyncPstates' 1; Ok "NVIDIA: async P-states off" }
$hasIntel = [bool]($gpus -match 'Intel')
if ((Use 'gpu.intel.flips') -and $hasIntel) { GpuSet 'Intel' 'Display1_DisableAsyncFlips' 1; Ok "Intel: async flips off" }
if ((Use 'gpu.intel.vsync') -and $hasIntel) { GpuSet 'Intel' 'AdaptiveVsyncEnable' 0; Ok "Intel: adaptive V-Sync off" }
Save-BK

# =====================================================================
#  4. UI / INPUT  (Atlas visual-effects + QoL)
# =====================================================================
if (Use 'ui.effects') {
    Step "Visual effects (Atlas preset)"
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
    Ok "done"
}
if (Use 'ui.transparency') {
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 0
    Ok "transparency off"
}
if (Use 'ui.menus') {
    Step "Snappy menus and shutdown"
    Set-Tweak 'HKCU:\Control Panel\Desktop' 'MenuShowDelay' '0' 'String'
    Set-Tweak 'HKCU:\Control Panel\Desktop' 'MouseHoverTime' '20' 'String'
    Set-Tweak 'HKCU:\Control Panel\Desktop' 'HungAppTimeout' '2000' 'String'
    Set-Tweak 'HKCU:\Control Panel\Desktop' 'WaitToKillAppTimeOut' '2000' 'String'
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control' 'WaitToKillServiceTimeout' '2000' 'String'
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize' 'StartupDelayInMSec' 0
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'Max Cached Icons' '4096' 'String'
    Ok "done"
}
if (Use 'ui.mouse') {
    Set-Tweak 'HKCU:\Control Panel\Mouse' 'MouseSpeed' '0' 'String'
    Set-Tweak 'HKCU:\Control Panel\Mouse' 'MouseThreshold1' '0' 'String'
    Set-Tweak 'HKCU:\Control Panel\Mouse' 'MouseThreshold2' '0' 'String'
    Ok "mouse acceleration off"
}
if (Use 'input.keyboard') {
    Set-Tweak 'HKCU:\Control Panel\Keyboard' 'KeyboardDelay' '0' 'String'
    Set-Tweak 'HKCU:\Control Panel\Keyboard' 'KeyboardSpeed' '31' 'String'
    Ok "keyboard: shortest repeat delay, fastest repeat rate"
}
if (Use 'input.stickykeys') {
    Set-Tweak 'HKCU:\Control Panel\Accessibility\StickyKeys' 'Flags' '26' 'String'
    Set-Tweak 'HKCU:\Control Panel\Accessibility\Keyboard Response' 'Flags' '2' 'String'
    Set-Tweak 'HKCU:\Control Panel\Accessibility\ToggleKeys' 'Flags' '34' 'String'
    Ok "Sticky/Filter/Toggle keys hotkeys off (no popup after pressing Shift 5 times in a game)"
}
if (Use 'ui.start') {
    Set-Tweak 'HKCU:\Software\Policies\Microsoft\Windows\Explorer' 'HideRecommendedSection' 1
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableConsumerAccountStateContent' 1
    Ok "Start: no 'Recommended' section and account nags"
}
if (Use 'ui.bgapps') {
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 1
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' 'BackgroundAppGlobalToggle' 0
    Ok "background apps off"
}
if (Use 'ui.search') {
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows Search\Gather\Windows\SystemIndex' 'RespectPowerModes' 1
    Ok "search indexer respects power modes"
}
Save-BK

# =====================================================================
#  5. TELEMETRY / BACKGROUND (Atlas privacy set)
# =====================================================================
if (Use 'privacy.telemetry') {
    Step "Telemetry off"
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
    Set-Tweak $dc 'DoNotShowFeedbackNotifications' 1
    Set-Tweak $dc 'AllowCommercialDataPipeline' 0
    Set-Tweak $dc 'MicrosoftEdgeDataOptIn' 0
    Set-Tweak 'HKCU:\SOFTWARE\Microsoft\Siuf\Rules' 'NumberOfSIUFInPeriod' 0
    Set-Tweak 'HKCU:\Software\Policies\Microsoft\Windows\EdgeUI' 'DisableMFUTracking' 1
    Set-Tweak 'HKCU:\SOFTWARE\Policies\Microsoft\Assistance\Client\1.0' 'NoExplicitFeedback' 1
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Assistance\Client\1.0' 'NoActiveHelp' 1
    Set-Tweak 'HKCU:\SOFTWARE\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy' 'HasAccepted' 0
    Ok "done"
}
if (Use 'privacy.appcompat') {
    $ac = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppCompat'
    foreach ($n in @{AITEnable=0;AllowTelemetry=0;DisableEngine=1;DisableInventory=1;DisablePCA=1;DisableUAR=1}.GetEnumerator()) { Set-Tweak $ac $n.Key $n.Value }
    Ok "compatibility tracking off"
}
if (Use 'privacy.wer') {
    $wer = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting'
    Set-Tweak $wer 'Disabled' 1; Set-Tweak $wer 'DontShowUI' 1; Set-Tweak $wer 'LoggingDisabled' 1; Set-Tweak $wer 'DontSendAdditionalData' 1
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'Disabled' 1
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports' 'PreventHandwritingErrorReports' 1
    Ok "error reporting off"
}
if (Use 'privacy.activity') {
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'EnableActivityFeed' 0
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'UploadUserActivities' 0
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'PublishUserActivities' 0
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'PublishUserActivitiesOnUserConsent' 0
    Set-Tweak 'HKCU:\SOFTWARE\Microsoft\InputPersonalization\TrainedDataStore' 'HarvestContacts' 0
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'Start_TrackProgs' 0
    Set-Tweak 'HKCU:\Software\Microsoft\InputPersonalization' 'RestrictImplicitInkCollection' 1
    Set-Tweak 'HKCU:\Software\Microsoft\InputPersonalization' 'RestrictImplicitTextCollection' 1
    Set-Tweak 'HKCU:\Software\Microsoft\Input\TIPC' 'Enabled' 0
    Ok "activity history and typing data off"
}
if (Use 'privacy.ads') {
    $cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
    foreach ($n in 'SilentInstalledAppsEnabled','SystemPaneSuggestionsEnabled','SubscribedContent-338387Enabled','SubscribedContent-338388Enabled','SubscribedContent-338389Enabled',
                   'SubscribedContent-353698Enabled','SoftLandingEnabled','ContentDeliveryAllowed','PreInstalledAppsEnabled','PreInstalledAppsEverEnabled','OemPreInstalledAppsEnabled') { Set-Tweak $cdm $n 0 }
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo' 'DisabledByGroupPolicy' 1
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo' 'Enabled' 0
    Set-Tweak 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy' 'TailoredExperiencesWithDiagnosticDataEnabled' 0
    $cc = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent'
    foreach ($n in 'DisableTailoredExperiencesWithDiagnosticData','DisableWindowsConsumerFeatures','DisableSoftLanding','DisableThirdPartySuggestions') { Set-Tweak $cc $n 1 }
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled' 0
    Set-Tweak 'HKCU:\Control Panel\International\User Profile' 'HttpAcceptLanguageOptOut' 1
    Ok "ads, suggestions and silent app installs off"
}
if (Use 'privacy.do') {
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode' 0
    Ok "Delivery Optimization P2P off"
}
if (Use 'privacy.ai') {
    Set-Tweak 'HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'ShowCopilotButton' 0
    foreach ($h in 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI', 'HKCU:\Software\Policies\Microsoft\Windows\WindowsAI') {
        Set-Tweak $h 'DisableAIDataAnalysis' 1
        Set-Tweak $h 'TurnOffSavingSnapshots' 1
        Set-Tweak $h 'DisableClickToDo' 1
    }
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI' 'AllowRecallEnablement' 0
    Ok "Copilot, Recall and Click to Do off"
}
if (Use 'privacy.widgets') {
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds' 'EnableFeeds' 0
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Feeds' 'ShellFeedsTaskbarViewMode' 2
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'HideSCAMeetNow' 1
    Ok "widgets, news and Meet Now off"
}
if (Use 'privacy.search') {
    $ws = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search'
    foreach ($n in 'AllowCortana','AllowCloudSearch','AllowCortanaAboveLock','AllowSearchToUseLocation','ConnectedSearchUseWeb') { Set-Tweak $ws $n 0 }
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' 'CortanaConsent' 0
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' 'BingSearchEnabled' 0
    Set-Tweak 'HKCU:\Software\Policies\Microsoft\Windows\Explorer' 'DisableSearchBoxSuggestions' 1
    Ok "Start search: local only (no Bing, no Cortana)"
}
if (Use 'privacy.autologger') {
    foreach ($l in 'AppModel','Cellcore','CloudExperienceHostOobe','DataMarket','DiagLog','LwtNetLog','SQMLogger','WdiContextLog','WiFiSession') {
        Try-Tweak "HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Autologger\$l" 'Start' 0
    }
    Ok "background diagnostic loggers off"
}
if (Use 'privacy.location') {
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors' 'DisableLocation' 1
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors' 'DisableSensors' 1
    Set-Tweak 'HKLM:\SYSTEM\Maps' 'AutoUpdateEnabled' 0
    Set-Tweak 'HKLM:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location' 'Value' 'Deny' 'String'
    Set-Tweak 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location' 'Value' 'Deny' 'String'
    Set-Tweak 'HKLM:\Software\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting' 'Value' 0
    Set-Tweak 'HKLM:\Software\Microsoft\PolicyManager\default\WiFi\AllowAutoConnectToWiFiSenseHotspots' 'Value' 0
    Ok "location, sensors and Wi-Fi Sense off"
}
if (Use 'privacy.findmydevice') {
    Set-Tweak 'HKLM:\SOFTWARE\Policies\Microsoft\FindMyDevice' 'AllowFindMyDevice' 0
    Ok "Find My Device off"
}
Save-BK

if (Use 'svc.telemetry') {
    Step "Telemetry services off"
    foreach ($s in 'DiagTrack','diagnosticshub.standardcollector.service','WerSvc','wercplsupport','PcaSvc','DcpSvc') { Set-Svc $s 4 }
    Set-Svc 'dmwappushservice' 3
}
if (Use 'svc.misc') {
    Step "Unused services off (Atlas list, minus anything that breaks LAN/sync)"
    foreach ($s in 'TrkWks','MapsBroker','RetailDemo','GpuEnergyDrv') { Set-Svc $s 4 }
}
if ($onHDD) { Ok "HDD detected: SysMain kept ON (it speeds up HDD systems)" }
if (Use 'svc.search') {
    if ($onHDD -and $ramGB -le 4) { Set-Svc 'WSearch' 3; Ok "weak HDD PC: search indexer set to manual" }
    else { Ok "search indexer kept (only changed on HDD + 4 GB RAM)" }
}
if (Use 'upd.pause') {
    Set-Tweak 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' 'FlightSettingsMaxPauseDays' 0xFFFFFFFF
    Ok "Windows Update can be paused for longer (Settings -> Windows Update)"
}
if (Use 'net.webdav') {
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters' 'FileSizeLimitInBytes' 0xFFFFFFFF
    Ok "WebDAV: no 50 MB file size limit"
}
if (Use 'tasks.telemetry') {
    Step "Scheduled telemetry tasks (Atlas list)"
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
}
Save-BK

# =====================================================================
#  6. APPS (Atlas appx list, minus useful ones; provisioned copies KEPT so undo works)
# =====================================================================
if (Use 'apps.junk') {
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
            $null = Remove-AppxTimed $_
        }
    }
    Ok "kept: Store, Xbox/Game Bar, Photos, Calculator, Camera, Snipping Tool, Notepad, Sticky Notes"
    Save-BK
}

# =====================================================================
#  7. OPTIONAL RISKY
# =====================================================================
if (Use 'risk.vbs') {
    Step "VBS / HVCI off"
    Misc-Once 'HypervisorLaunch' $(if ((bcdedit /enum '{current}') -match 'hypervisorlaunchtype\s+Off') { 'off' } else { 'auto' })
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' 'EnableVirtualizationBasedSecurity' 0
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled' 0
    bcdedit /set hypervisorlaunchtype off | Out-Null
    Ok "done"
}
if (Use 'risk.mitigations') {
    Step "Spectre/Meltdown mitigations off (Atlas values)"
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'FeatureSettingsOverride' 3
    Set-Tweak 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'FeatureSettingsOverrideMask' 3
    Ok "done (process mitigations/CFG left ON - anticheats like Vanguard need them)"
}
if (Use 'risk.services') {
    Step "Services: rarely used ones -> manual start (WinUtil list)"
    # Manual = Windows starts them on demand (trigger start); only services currently set to Automatic are changed
    $manual = 'ALG AppIDSvc AppMgmt AppReadiness AppXSvc Appinfo AxInstSV BDESVC BTAGService Browser CDPSvc COMSysApp CertPropSvc ClipSVC CscService DcpSvc DevQueryBroker DeviceAssociationService DeviceInstall DisplayEnhancementService DmEnrollmentSvc EFS EapHost EntAppSvc FDResPub FrameServer FrameServerMonitor GraphicsPerfSvc HvHost IEEtwCollectorService IKEEXT InstallService InventorySvc IpxlatCfgSvc KtmRm LanmanServer LicenseManager LxpSvc MSDTC MSiSCSI McpManagementService MicrosoftEdgeElevationService MixedRealityOpenXRSvc MsKeyboardFilter NaturalAuthentication NcaSvc NcbService NcdAutoSetup NetSetupSvc NgcCtnrSvc NgcSvc PNRPAutoReg PNRPsvc PcaSvc PeerDistSvc PerfHost PhoneSvc PolicyAgent PrintNotify PushToInstall QWAVE RasAuto RasMan RmSvc RpcLocator SCPolicySvc SCardSvr SDRSVC SEMgrSvc SNMPTRAP SSDPSRV ScDeviceEnum SensorDataService SensorService SensrSvc SessionEnv SharedAccess SharedRealitySvc SmsRouter Spooler SstpSvc StiSvc StorSvc TabletInputService TapiSrv TermService TieringEngineService TimeBroker TimeBrokerSvc TokenBroker TroubleshootingSvc UI0Detect UmRdpService UsoSvc VGAuthService VMTools VSS VacSvc W32Time WEPHOSTSVC WFDSConMgrSvc WMPNetworkSvc WManSvc WPDBusEnum WSearch WaaSMedicSvc WalletService WarpJITSvc WbioSrvc WcsPlugInService WdiServiceHost WdiSystemHost WebClient Wecsvc WerSvc WiaRpc WinHttpAutoProxySvc WinRM WpcMonSvc WpnService XblAuthManager XblGameSave XboxGipSvc XboxNetApiSvc autotimesvc bthserv camsvc cloudidsvc dcsvc defragsvc diagnosticshub.standardcollector.service diagsvc dmwappushservice dot3svc edgeupdate edgeupdatem embeddedmode fdPHost fhsvc hidserv icssvc lfsvc lltdsvc lmhosts msiserver netprofm p2pimsvc p2psvc perceptionsimulation pla seclogon smphost spectrum svsvc swprv upnphost vds vm3dservice vmicguestinterface vmicheartbeat vmickvpexchange vmicrdv vmicshutdown vmictimesync vmicvmsession vmicvss vmvss wbengine wcncsvc webthreatdefsvc wercplsupport wisvc wlidsvc wlpasvc wmiApSrv workfolderssvc wuauserv' -split ' '
    $n = 0
    foreach ($s in $manual) {
        $p = "HKLM:\SYSTEM\CurrentControlSet\Services\$s"
        if ((Test-Path $p) -and (Get-ItemProperty $p).Start -eq 2) { Try-Tweak $p 'Start' 3; $n++ }
    }
    foreach ($s in 'AJRouter','AppVClient','AssignedAccessManagerSvc','DialogBlockingService','Fax','HomeGroupListener','HomeGroupProvider','NetTcpPortSharing','RemoteAccess','RemoteRegistry','RetailDemo','UevAgentService','shpamsvc') { Set-Svc $s 4 }
    Ok "$n automatic service(s) switched to manual (start on demand)"
}
Save-BK

# =====================================================================
#  8. SOFTWARE + NVIDIA PROFILE
# =====================================================================
$haveWinget = [bool](Get-Command winget.exe -ErrorAction SilentlyContinue)
function Install-App($id) {
    if (-not $haveWinget) { Warn "winget missing - skip $id"; return }
    winget.exe install --id $id -e --silent --accept-package-agreements --accept-source-agreements | Out-Null
    # 0 = ok, 0x8A15002B = no newer version, 0x8A150061 = already installed
    if ($LASTEXITCODE -in 0, -1978335189, -1978335135) { Ok "installed: $id" } else { Warn "could not install $id" }
}
if (((Use 'soft.runtimes') -or (Use 'soft.tools') -or (Use 'soft.afterburner') -or (Use 'soft.vendor')) -and -not $haveWinget) {
    Warn "winget not found: install 'App Installer' from Microsoft Store, then re-run for apps"
}
if (Use 'soft.runtimes') {
    Step "Runtimes for games (fixes missing DLL errors)"
    'Microsoft.VCRedist.2015+.x64','Microsoft.VCRedist.2015+.x86','Microsoft.DirectX' | ForEach-Object { Install-App $_ }
}
if (Use 'soft.tools') {
    Step "Tools"
    'REALiX.HWiNFO','BitSum.ProcessLasso','Microsoft.Sysinternals.Autoruns','CrystalDewWorld.CrystalDiskInfo','7zip.7zip' | ForEach-Object { Install-App $_ }
}
if ((Use 'soft.afterburner') -and ($hasNV -or $hasAMD)) { Step "MSI Afterburner"; Install-App 'Guru3D.Afterburner' }
if (Use 'soft.vendor') {
    if ($vendor -match 'ASUS')   { Step "G-Helper (ASUS)"; Install-App 'seerge.g-helper' }
    if ($vendor -match 'LENOVO') { Step "Legion Toolkit (Lenovo)"; Install-App 'BartoszCichecki.LenovoLegionToolkit' }
}
if ((Use 'soft.nvidia') -and $hasNV) {
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
Save-BK

# =====================================================================
#  9. CLEANUP + REPORT
# =====================================================================
if (Use 'clean.temp') {
    Step "Cleanup: temp files, component store, TRIM"
    Remove-Item "$env:TEMP\*", "$env:SystemRoot\Temp\*" -Recurse -Force -ErrorAction SilentlyContinue
    Ok "temp files removed (files in use are skipped)"
    ipconfig /flushdns | Out-Null
    if (-not $onHDD) {
        if (Run-Long 'TRIM' "$env:SystemRoot\System32\defrag.exe" "$($env:SystemDrive) /L" 300) { Ok "TRIM done" }
    } else { Ok "HDD: run defrag weekly (Windows does it automatically)" }
    # Component store cleanup can take 10-60 min on a PC with many updates; cap it so the run never looks frozen
    if (Run-Long 'Component store cleanup (DISM)' "$env:SystemRoot\System32\Dism.exe" '/Online /Cleanup-Image /StartComponentCleanup /Quiet /NoRestart' 900) { Ok "old update components removed" }
    Ok "done"
}

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
