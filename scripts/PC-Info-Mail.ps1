<#
  PC-Info-Mail.ps1 - collects full PC specs + health and emails an HTML report via Gmail.

  ONE-TIME SETUP (Gmail):
    1) Google account -> Security -> 2-Step Verification must be ON
    2) https://myaccount.google.com/apppasswords -> create app password "PC-Info" (16 chars)
    3) First run asks for your Gmail address, the recipient and the app password.
       The password is stored ENCRYPTED for this Windows user (DPAPI), next runs are silent.

  Run (PowerShell as Administrator for full disk/battery info):
      Set-ExecutionPolicy -Scope Process Bypass -Force; .\PC-Info-Mail.ps1
  Other recipient:  .\PC-Info-Mail.ps1 -To someone@mail.com
  Reset password:   .\PC-Info-Mail.ps1 -ResetPassword   (also asks for the addresses again)
  Behind VPN / blocked SMTP: script auto-tries port 587 -> port 465 -> Telegram bot (HTTPS).
  Force a method:   .\PC-Info-Mail.ps1 -Method Telegram   (Smtp587 | Smtp465 | Telegram)
  Reset Telegram:   .\PC-Info-Mail.ps1 -ResetTelegram
#>
param(
    [string]$To,
    [string]$From,
    [switch]$ResetPassword,
    [switch]$ResetTelegram,
    [switch]$NoMail,
    [ValidateSet('Auto','Smtp587','Smtp465','Telegram')]
    [string]$Method = 'Auto'
)
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference    = 'SilentlyContinue'

$credFile = Join-Path $env:LOCALAPPDATA 'PCInfoMail.cred'
$addrFile = Join-Path $env:LOCALAPPDATA 'PCInfoMail.json'
$outDir   = Join-Path $env:TEMP "PCInfo_$env:COMPUTERNAME"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function Esc($s) { [System.Net.WebUtility]::HtmlEncode([string]$s) }
function Table($title, $rows) {
    $h = "<h2>$(Esc $title)</h2><table>"
    foreach ($r in $rows) {
        if ($r -is [System.Collections.IDictionary]) { foreach ($k in $r.Keys) { $h += "<tr><th>$(Esc $k)</th><td>$(Esc $r[$k])</td></tr>" } ; $h += "<tr class='sep'><td colspan=2></td></tr>" }
    }
    $h + "</table>"
}

Write-Host "Collecting info..." -ForegroundColor Cyan

# ---------------------------------------------------------------- SYSTEM
$cs   = Get-CimInstance Win32_ComputerSystem
$os   = Get-CimInstance Win32_OperatingSystem
$bios = Get-CimInstance Win32_BIOS
$mb   = Get-CimInstance Win32_BaseBoard
$dv   = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion
$up   = (Get-Date) - $os.LastBootUpTime
$plan = (powercfg /getactivescheme) -replace '.*\((.*)\).*','$1'
$dg   = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard
$sys = [ordered]@{
    'Computer name' = $env:COMPUTERNAME
    'User'          = $env:USERNAME
    'Laptop'        = "$($cs.Manufacturer) $($cs.Model)"
    'Serial / Tag'  = $bios.SerialNumber
    'Motherboard'   = "$($mb.Manufacturer) $($mb.Product)"
    'BIOS'          = "$($bios.SMBIOSBIOSVersion) ($($bios.ReleaseDate.ToString('yyyy-MM-dd')))"
    'Windows'       = "$($os.Caption) $dv (build $($os.BuildNumber))"
    'Installed'     = $os.InstallDate.ToString('yyyy-MM-dd')
    'Uptime'        = "{0}d {1}h {2}m" -f $up.Days, $up.Hours, $up.Minutes
    'Power plan'    = $plan
    'VBS'           = @('Off','Enabled (not running)','Running')[[int]$dg.VirtualizationBasedSecurityStatus]
    'Secure Boot'   = $(try { (Confirm-SecureBootUEFI -ErrorAction Stop) -as [string] } catch { 'n/a (legacy BIOS)' })
}

# ---------------------------------------------------------------- CPU
$cpu = @(foreach ($c in Get-CimInstance Win32_Processor) { [ordered]@{
    'Model'       = $c.Name.Trim()
    'Cores / Threads' = "$($c.NumberOfCores) / $($c.NumberOfLogicalProcessors)"
    'Max clock'   = "$($c.MaxClockSpeed) MHz"
    'Current load'= "$($c.LoadPercentage) %"
    'L2 / L3'     = "$([math]::Round($c.L2CacheSize/1024,1)) MB / $([math]::Round($c.L3CacheSize/1024,1)) MB"
    'Virtualization in BIOS' = $c.VirtualizationFirmwareEnabled
}})

# ---------------------------------------------------------------- GPU
$gpu = @(foreach ($g in Get-CimInstance Win32_VideoController) {
    $vram = $null
    $reg = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' |
           Where-Object { (Get-ItemProperty $_.PSPath).DriverDesc -eq $g.Name } | Select-Object -First 1
    if ($reg) { $q = (Get-ItemProperty $reg.PSPath).'HardwareInformation.qwMemorySize'; if ($q) { $vram = "$([math]::Round($q/1GB,1)) GB" } }
    [ordered]@{
        'Model'      = $g.Name
        'VRAM'       = if ($vram) { $vram } else { "$([math]::Round($g.AdapterRAM/1GB,1)) GB (WMI, may be capped at 4)" }
        'Driver'     = "$($g.DriverVersion) ($($g.DriverDate.ToString('yyyy-MM-dd')))"
        'Resolution' = if ($g.CurrentHorizontalResolution) { "$($g.CurrentHorizontalResolution)x$($g.CurrentVerticalResolution) @ $($g.CurrentRefreshRate) Hz" } else { 'inactive' }
    }
})
$nvsmi = "$env:SystemRoot\System32\nvidia-smi.exe"
if (Test-Path $nvsmi) {
    $n = (& $nvsmi --query-gpu=temperature.gpu,power.draw,clocks.gr,utilization.gpu --format=csv,noheader) -split ','
    $gpu += [ordered]@{ 'NVIDIA live' = "Temp $($n[0].Trim()) C | Power $($n[1].Trim()) | Clock $($n[2].Trim()) | Load $($n[3].Trim())" }
}

# ---------------------------------------------------------------- RAM
$sticks = Get-CimInstance Win32_PhysicalMemory
$ram = @([ordered]@{
    'Total'   = "$([math]::Round($cs.TotalPhysicalMemory/1GB,1)) GB"
    'In use'  = "$([math]::Round(($os.TotalVisibleMemorySize-$os.FreePhysicalMemory)/1MB,1)) GB"
    'Sticks'  = "$($sticks.Count) -> $(if ($sticks.Count -ge 2) {'dual channel'} else {'SINGLE CHANNEL!'})"
})
$ram += foreach ($m in $sticks) { [ordered]@{
    'Slot'  = $m.DeviceLocator
    'Size'  = "$($m.Capacity/1GB) GB"
    'Speed' = "$($m.ConfiguredClockSpeed) MHz (rated $($m.Speed))"
    'Maker / Part' = "$($m.Manufacturer) $($m.PartNumber.Trim())"
}}

# ---------------------------------------------------------------- DISKS (with SMART health)
$disk = foreach ($d in Get-PhysicalDisk) {
    $r = $d | Get-StorageReliabilityCounter
    [ordered]@{
        'Model'       = $d.FriendlyName
        'Type / Bus'  = "$($d.MediaType) / $($d.BusType)"
        'Size'        = "$([math]::Round($d.Size/1GB)) GB"
        'Health'      = "$($d.HealthStatus) ($($d.OperationalStatus))"
        'Temperature' = if ($r.Temperature) { "$($r.Temperature) C" } else { 'n/a' }
        'Wear used'   = if ($null -ne $r.Wear) { "$($r.Wear) %" } else { 'n/a' }
        'Power-on hours' = $r.PowerOnHours
        'Read/Write errors' = "$($r.ReadErrorsTotal) / $($r.WriteErrorsTotal)"
    }
}
$vol = foreach ($v in Get-Volume | Where-Object { $_.DriveLetter -and $_.Size -gt 0 }) { [ordered]@{
    'Drive' = "$($v.DriveLetter): $($v.FileSystemLabel)"
    'Free'  = "$([math]::Round($v.SizeRemaining/1GB,1)) of $([math]::Round($v.Size/1GB,1)) GB ($([math]::Round($v.SizeRemaining/$v.Size*100))%)"
}}

# ---------------------------------------------------------------- BATTERY
$bat = @()
$b = Get-CimInstance Win32_Battery
if ($b) {
    $design = (Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData).DesignedCapacity | Select-Object -First 1
    $full   = (Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity).FullChargedCapacity | Select-Object -First 1
    $cyc    = (Get-CimInstance -Namespace root\wmi -ClassName BatteryCycleCount).CycleCount | Select-Object -First 1
    $bat = @([ordered]@{
        'Charge now'     = "$($b.EstimatedChargeRemaining) %"
        'Plugged in'     = ($b.BatteryStatus -eq 2)
        'Design capacity'= "$design mWh"
        'Full charge now'= "$full mWh"
        'Battery health' = if ($design) { "$([math]::Round($full/$design*100)) %" } else { 'n/a' }
        'Cycles'         = if ($cyc) { $cyc } else { 'n/a' }
    })
    powercfg /batteryreport /output "$outDir\battery-report.html" | Out-Null
}

# ---------------------------------------------------------------- NETWORK
$net = foreach ($a in Get-NetAdapter | Where-Object Status -eq 'Up') {
    $ip = (Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4).IPAddress
    [ordered]@{ 'Adapter' = $a.InterfaceDescription; 'Speed' = $a.LinkSpeed; 'MAC' = $a.MacAddress; 'Local IP' = ($ip -join ', ') }
}

# ---------------------------------------------------------------- TEMPS / HEALTH
$thermal = Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature
$health = @([ordered]@{
    'ACPI temp'         = if ($thermal) { ($thermal | ForEach-Object { "$([math]::Round($_.CurrentTemperature/10-273.15)) C" }) -join ', ' } else { 'n/a (use HWiNFO)' }
    'Crashes (BSOD) 30d'= (Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,1001; StartTime=(Get-Date).AddDays(-30)} | Measure-Object).Count
    'App errors 7d'     = (Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2; StartTime=(Get-Date).AddDays(-7)} | Measure-Object).Count
    'Pending reboot'    = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
    'Defender'          = "RealTime=$((Get-MpComputerStatus).RealTimeProtectionEnabled), Sigs=$((Get-MpComputerStatus).AntivirusSignatureLastUpdated)"
})
$startup = Get-CimInstance Win32_StartupCommand | ForEach-Object { [ordered]@{ 'Startup' = $_.Name; 'Command' = $_.Command } }
$topProc = Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 |
           ForEach-Object { [ordered]@{ 'Process' = $_.ProcessName; 'RAM' = "$([math]::Round($_.WorkingSet64/1MB)) MB" } }

# ---------------------------------------------------------------- BUILD HTML
$css = "body{font-family:Segoe UI,Arial;background:#f4f4f6;color:#222;max-width:820px;margin:auto;padding:16px}
h1{font-size:22px}h2{font-size:16px;margin-top:22px;border-bottom:2px solid #444;padding-bottom:3px}
table{border-collapse:collapse;width:100%;background:#fff}th{text-align:left;width:34%;background:#eee;font-weight:600}
th,td{padding:5px 8px;border:1px solid #ddd;font-size:13px;vertical-align:top}tr.sep td{border:none;height:6px;background:#f4f4f6}"
$html = "<html><head><meta charset='utf-8'><style>$css</style></head><body>"
$html += "<h1>$(Esc "$env:COMPUTERNAME - $($cs.Manufacturer) $($cs.Model)")</h1><p>Report: $(Get-Date -f 'yyyy-MM-dd HH:mm')</p>"
$html += Table 'System' @($sys)
$html += Table 'CPU' @($cpu)
$html += Table 'GPU' @($gpu)
$html += Table 'RAM' @($ram)
$html += Table 'Physical disks' @($disk)
$html += Table 'Volumes' @($vol)
if ($bat) { $html += Table 'Battery' @($bat) }
$html += Table 'Network' @($net)
$html += Table 'Health' @($health)
$html += Table 'Top 10 processes by RAM' @($topProc)
$html += Table 'Startup programs' @($startup)
$html += "</body></html>"

$reportFile = "$outDir\PC-Report_${env:COMPUTERNAME}_$(Get-Date -f yyyyMMdd_HHmm).html"
$html | Out-File $reportFile -Encoding utf8
Write-Host "Report saved: $reportFile" -ForegroundColor Green

if ($NoMail) { Start-Process $reportFile; exit }

# ---------------------------------------------------------------- SEND
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$subject = "PC report: $env:COMPUTERNAME ($($cs.Manufacturer) $($cs.Model))"
$att = @($reportFile); if (Test-Path "$outDir\battery-report.html") { $att += "$outDir\battery-report.html" }
$tgFile = Join-Path $env:LOCALAPPDATA 'PCInfoTelegram.cred'

function Get-MailAddr {
    # sender / recipient: -From/-To params > saved config > ask once and remember
    if ($ResetPassword -and (Test-Path $addrFile)) { Remove-Item $addrFile -Force }
    $saved = if (Test-Path $addrFile) { Get-Content $addrFile -Raw | ConvertFrom-Json } else { $null }
    if (-not $script:From) { $script:From = $saved.From }
    if (-not $script:From) { $script:From = (Read-Host "Your Gmail address (sender)").Trim() }
    if (-not $script:To)   { $script:To = $saved.To }
    if (-not $script:To)   { $t = (Read-Host "Send report to [Enter = $script:From]").Trim(); $script:To = if ($t) { $t } else { $script:From } }
    if ($script:From -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$' -or $script:To -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw "invalid e-mail address" }
    [pscustomobject]@{ From = $script:From; To = $script:To } | ConvertTo-Json | Set-Content $addrFile -Encoding UTF8
}

function Get-MailCred {
    Get-MailAddr
    if ($ResetPassword -and (Test-Path $credFile)) { Remove-Item $credFile -Force }
    if (Test-Path $credFile) { $c = Import-Clixml $credFile }
    else {
        $pw = Read-Host "Gmail APP password for $From (16 chars)" -AsSecureString
        $c = New-Object System.Management.Automation.PSCredential($From, $pw)
        $c | Export-Clixml $credFile
    }
    # app passwords are shown with spaces - strip them
    $plain = $c.GetNetworkCredential().Password -replace '\s',''
    New-Object System.Management.Automation.PSCredential($From, (ConvertTo-SecureString $plain -AsPlainText -Force))
}

function Send-Smtp587 {
    $cred = Get-MailCred
    Send-MailMessage -SmtpServer 'smtp.gmail.com' -Port 587 -UseSsl -Credential $cred `
        -From $From -To $To -Subject $subject -Body $html -BodyAsHtml -Encoding UTF8 -Attachments $att -ErrorAction Stop
}

function Send-Smtp465 {
    # Implicit TLS on 465 (Send-MailMessage can't do it) - raw SMTP over SslStream
    $cred = Get-MailCred
    $user = $cred.UserName; $pass = $cred.GetNetworkCredential().Password
    $tcp = New-Object Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect('smtp.gmail.com', 465, $null, $null)
    if (-not $iar.AsyncWaitHandle.WaitOne(10000)) { throw "port 465 timeout" }
    $tcp.EndConnect($iar)
    $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false)
    $ssl.ReadTimeout = 30000
    $ssl.AuthenticateAsClient('smtp.gmail.com')
    $r = New-Object IO.StreamReader($ssl)
    $w = New-Object IO.StreamWriter($ssl); $w.NewLine = "`r`n"; $w.AutoFlush = $true
    $reply = { do { $l = $r.ReadLine() } while ($l -match '^\d{3}-'); if ($l -notmatch '^[23]') { throw "SMTP: $l" }; $l }
    $cmd   = { param($c) $w.WriteLine($c); & $reply }
    try {
        & $reply | Out-Null
        & $cmd "EHLO $env:COMPUTERNAME" | Out-Null
        $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("`0$user`0$pass"))
        & $cmd "AUTH PLAIN $auth" | Out-Null
        & $cmd "MAIL FROM:<$From>" | Out-Null
        & $cmd "RCPT TO:<$To>" | Out-Null
        & $cmd "DATA" | Out-Null
        $b = "----=_PCInfo_" + [guid]::NewGuid().ToString('N')
        $sb = New-Object Text.StringBuilder
        $subj = "=?UTF-8?B?" + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($subject)) + "?="
        [void]$sb.Append("From: <$From>`r`nTo: <$To>`r`nSubject: $subj`r`nDate: $((Get-Date).ToString('r'))`r`nMIME-Version: 1.0`r`nContent-Type: multipart/mixed; boundary=`"$b`"`r`n`r`n")
        [void]$sb.Append("--$b`r`nContent-Type: text/html; charset=utf-8`r`nContent-Transfer-Encoding: base64`r`n`r`n")
        [void]$sb.Append([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($html), 'InsertLineBreaks') + "`r`n")
        foreach ($f in $att) {
            $n = [IO.Path]::GetFileName($f)
            [void]$sb.Append("--$b`r`nContent-Type: text/html; name=`"$n`"`r`nContent-Disposition: attachment; filename=`"$n`"`r`nContent-Transfer-Encoding: base64`r`n`r`n")
            [void]$sb.Append([Convert]::ToBase64String([IO.File]::ReadAllBytes($f), 'InsertLineBreaks') + "`r`n")
        }
        [void]$sb.Append("--$b--`r`n.")
        & $cmd $sb.ToString() | Out-Null
        $w.WriteLine("QUIT")
    } finally { $ssl.Dispose(); $tcp.Close() }
}

function Get-TgConfig {
    if ($ResetTelegram -and (Test-Path $tgFile)) { Remove-Item $tgFile -Force }
    if (Test-Path $tgFile) { $c = Import-Clixml $tgFile; return @{ Chat = $c.UserName; Token = $c.GetNetworkCredential().Password } }
    Write-Host "`n--- Telegram setup (one time) ---" -ForegroundColor Cyan
    Write-Host "1) In Telegram open @BotFather -> /newbot -> copy the token"
    $token = (Read-Host "2) Paste bot token").Trim()
    $me = Invoke-RestMethod "https://api.telegram.org/bot$token/getMe"   # throws if token is wrong
    Invoke-RestMethod "https://api.telegram.org/bot$token/deleteWebhook" | Out-Null
    Write-Host "3) Bot OK: @$($me.result.username). Open it in Telegram and send ANY message (or press Start)." -ForegroundColor Yellow
    Write-Host "   Waiting up to 2 minutes..." -NoNewline
    $chat = $null
    for ($i = 0; $i -lt 24 -and -not $chat; $i++) {
        $u = Invoke-RestMethod "https://api.telegram.org/bot$token/getUpdates?timeout=5"
        $chat = ($u.result | ForEach-Object { if ($_.message) { $_.message.chat.id } elseif ($_.my_chat_member) { $_.my_chat_member.chat.id } } |
                 Select-Object -Last 1)
        if (-not $chat) { Write-Host "." -NoNewline }
    }
    Write-Host ""
    if (-not $chat) { throw "no message received from you in 2 minutes - run again with -Method Telegram" }
    New-Object System.Management.Automation.PSCredential("$chat", (ConvertTo-SecureString $token -AsPlainText -Force)) | Export-Clixml $tgFile
    Write-Host "Saved (chat id $chat)" -ForegroundColor Green
    @{ Chat = "$chat"; Token = $token }
}

function Send-Telegram {
    $tg = Get-TgConfig
    Add-Type -AssemblyName System.Net.Http
    $hc = [Net.Http.HttpClient]::new(); $hc.Timeout = [TimeSpan]::FromSeconds(60)
    $short = "PC report: $env:COMPUTERNAME`n$($sys['Laptop'])`nCPU: $($cpu[0]['Model'])`nGPU: $(($gpu | ForEach-Object { $_['Model'] } | Where-Object { $_ }) -join ' + ')`nRAM: $($ram[0]['Total']), $($ram[0]['Sticks'])`nWindows: $($sys['Windows'])"
    if ($bat) { $short += "`nBattery health: $($bat[0]['Battery health'])" }
    foreach ($f in $att) {
        $mp = [Net.Http.MultipartFormDataContent]::new()
        $mp.Add([Net.Http.StringContent]::new($tg.Chat), 'chat_id')
        if ($f -eq $att[0]) { $mp.Add([Net.Http.StringContent]::new($short), 'caption') }
        $fc = [Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($f))
        $mp.Add($fc, 'document', [IO.Path]::GetFileName($f))
        $res = $hc.PostAsync("https://api.telegram.org/bot$($tg.Token)/sendDocument", $mp).Result
        if (-not $res.IsSuccessStatusCode) { throw "Telegram: $($res.Content.ReadAsStringAsync().Result)" }
    }
}

$methods = if ($Method -eq 'Auto') { 'Smtp587','Smtp465','Telegram' } else { @($Method) }
$sent = $false
foreach ($m in $methods) {
    if ($m -eq 'Telegram' -and $Method -eq 'Auto' -and -not (Test-Path $tgFile)) {
        if ((Read-Host "Mail ports are blocked (VPN?). Send to Telegram bot instead? [y/N]") -notmatch '^(y|yes|d|da)$') { break }
    }
    Write-Host "Trying $m..." -ForegroundColor Cyan
    try {
        & "Send-$m"
        $dest = if ($m -eq 'Telegram') { 'Telegram' } else { $To }
        Write-Host "Sent via $m to $dest" -ForegroundColor Green
        $sent = $true; break
    } catch {
        Write-Host "  $m failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if (-not $sent) {
    Write-Host "Could not send. Report is saved locally and opening now." -ForegroundColor Red
    Write-Host "Tip: in VPN app enable split tunneling and exclude powershell.exe, or run -Method Telegram." -ForegroundColor Yellow
    Start-Process $reportFile
}
