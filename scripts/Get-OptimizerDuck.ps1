<#
=====================================================================
  Get-OptimizerDuck.ps1
  Downloads the latest optimizerDuck release (https://github.com/itsfatduck/optimizerDuck)
  - picks the right build for this PC (x64 / ARM64)
  - verifies the SHA-256 checksum published by GitHub (when available)
  - saves it to C:\Tools\optimizerDuck and (optionally) starts it

  optimizerDuck is a separate open-source project (GPL-3.0) by itsfatduck.
  It is NOT bundled here - it is always downloaded fresh from its official GitHub releases.

  Usage (PowerShell):
     .\Get-OptimizerDuck.ps1              download + start
     .\Get-OptimizerDuck.ps1 -NoLaunch    download only
     .\Get-OptimizerDuck.ps1 -Force       re-download even if the latest version is already there
=====================================================================
#>
param([switch]$NoLaunch, [switch]$Force, [string]$Dest = (Join-Path $env:SystemDrive 'Tools\optimizerDuck'))

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo     = 'itsfatduck/optimizerDuck'
$Releases = "https://github.com/$Repo/releases/latest"

$GUI = [bool]$env:UO_GUI
function Ok($t)   { if ($GUI) { Write-Host "##ok $t" }   else { Write-Host "    $t" -ForegroundColor Green } }
function Warn($t) { if ($GUI) { Write-Host "##warn $t" } else { Write-Host "    $t" -ForegroundColor Yellow } }

$build = [Environment]::OSVersion.Version.Build
if ($build -lt 10240) { Warn "optimizerDuck needs Windows 10/11 (this is build $build)"; return }

# Real CPU architecture even if we run inside 32-bit PowerShell
$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$want = switch ($arch) { 'ARM64' { 'arm64' } 'AMD64' { 'x64' } default { $null } }
if (-not $want) { Warn "optimizerDuck has no build for $arch Windows (only x64 / ARM64)"; return }

try {
    $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing `
           -Headers @{ 'User-Agent' = 'Ultimate-Optimizer'; 'Accept' = 'application/vnd.github+json' }
} catch {
    Warn "GitHub API unreachable ($($_.Exception.Message))"
    Warn "Download it manually: $Releases"
    if (-not $NoLaunch) { Start-Process $Releases }
    return
}

$exeAssets = @($rel.assets | Where-Object { $_.name -like '*.exe' })
$asset = $exeAssets | Where-Object { $_.name -match "(?i)[-_.]$want[-_.]" } | Select-Object -First 1
if (-not $asset -and $want -eq 'x64') { $asset = $exeAssets | Where-Object { $_.name -notmatch '(?i)arm64' } | Select-Object -First 1 }
if (-not $asset) { Warn "no $want .exe in release $($rel.tag_name) - opening $Releases"; if (-not $NoLaunch) { Start-Process $Releases }; return }

New-Item -ItemType Directory -Path $Dest -Force | Out-Null
$exe = Join-Path $Dest $asset.name

if ((Test-Path $exe) -and -not $Force -and (Get-Item $exe).Length -eq $asset.size) {
    Ok "optimizerDuck $($rel.tag_name) already downloaded: $exe"
} else {
    Write-Host "    downloading $($asset.name) ($([math]::Round($asset.size/1MB,1)) MB)..."
    $tmp = "$exe.part"
    try {
        Invoke-WebRequest $asset.browser_download_url -OutFile $tmp -UseBasicParsing -Headers @{ 'User-Agent' = 'Ultimate-Optimizer' }
    } catch {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        Warn "download failed ($($_.Exception.Message)) - get it manually: $Releases"
        return
    }
    # GitHub publishes "sha256:<hex>" for release assets - verify when present
    if ("$($asset.digest)" -match '^sha256:([0-9a-fA-F]{64})$') {
        $got = (Get-FileHash $tmp -Algorithm SHA256).Hash
        if ($got -ne $Matches[1].ToUpper()) {
            Remove-Item $tmp -Force
            Warn "SHA-256 mismatch - file deleted, NOT started. Try again later or download manually: $Releases"
            return
        }
        Ok "SHA-256 verified"
    } elseif ((Get-Item $tmp).Length -ne $asset.size) {
        Remove-Item $tmp -Force
        Warn "size mismatch - download corrupted, try again"
        return
    }
    # remove older versions, keep only the latest
    Get-ChildItem $Dest -Filter 'optimizerDuck*.exe' | Where-Object { $_.FullName -ne $exe } | Remove-Item -Force -ErrorAction SilentlyContinue
    Move-Item $tmp $exe -Force
    Unblock-File $exe -ErrorAction SilentlyContinue
    Ok "optimizerDuck $($rel.tag_name) -> $exe"
}

if (-not $NoLaunch) {
    Ok "starting optimizerDuck (it asks for administrator rights itself)"
    Start-Process $exe -WorkingDirectory $Dest
}
