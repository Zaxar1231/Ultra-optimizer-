<#
  build.ps1 - builds dist\UltimateOptimizer.exe (WPF, Material Design 3 UI) and dist\UltimateOptimizer-v<ver>.zip

  Windows: uses csc.exe from .NET Framework 4 (already in every Windows 10/11)
  Linux:   uses mcs (apt install mono-mcs) + .NET 4.8 reference assemblies (-RefDir) - for local checks;
           release builds run on GitHub Actions

     pwsh ./build.ps1            (or: powershell -ExecutionPolicy Bypass -File build.ps1)
#>
param([string]$Version = (Get-Content (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim(), [string]$RefDir)

$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$src   = Join-Path $root 'scripts'
$dist  = Join-Path $root 'dist'
$stage = Join-Path $dist 'UltimateOptimizer'
$gen   = Join-Path $dist 'obj'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must look like 1.2.3, got '$Version'" }
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage, $gen -Force | Out-Null

# ---- 1. stage scripts with Windows line endings (.bat with LF breaks cmd.exe)
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$utf8Bom   = New-Object Text.UTF8Encoding($true)
foreach ($f in Get-ChildItem $src -File) {
    $out = Join-Path $stage $f.Name
    if ($f.Extension -in '.ps1', '.bat', '.cmd', '.txt') {
        $text = [IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n" -replace "`n", "`r`n"
        if ($f.Extension -in '.ps1', '.bat' -and $text -match '[^\x00-\x7F]') {
            throw "$($f.Name) contains non-ASCII characters (Windows PowerShell 5.1 / cmd.exe would garble them)"
        }
        # README.txt is Russian -> BOM so old Notepad shows it correctly
        [IO.File]::WriteAllText($out, $text, $(if ($f.Extension -eq '.txt') { $utf8Bom } else { $utf8NoBom }))
    } else {
        Copy-Item $f.FullName $out
    }
}

# ---- 1b. the tweak catalog (app) and the script must know the same tweak ids
$cat = Get-Content (Join-Path $root 'launcher/ui/tweaks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$catIds = @($cat.groups | ForEach-Object { $_.items } | ForEach-Object { $_.id })
$scriptText = Get-Content (Join-Path $src 'Ultimate-Optimizer.ps1') -Raw
$scriptIds = @([regex]::Matches($scriptText, "Use '([a-z.]+)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$profIds = @($cat.profiles | ForEach-Object { @($_.exclude) + @($_.include) } | Where-Object { $_ })
$bad = @($catIds | Where-Object { $_ -notin $scriptIds } | ForEach-Object { "in tweaks.json but not in the script: $_" }) +
       @($scriptIds | Where-Object { $_ -notin $catIds } | ForEach-Object { "in the script but not in tweaks.json: $_" }) +
       @($profIds | Where-Object { $_ -notin $catIds } | ForEach-Object { "profile uses unknown id: $_" }) +
       @($catIds | Group-Object | Where-Object Count -gt 1 | ForEach-Object { "duplicate id: $($_.Name)" })
if ($bad.Count) { throw ("tweak catalog mismatch:`n" + ($bad -join "`n")) }
Write-Host "tweak catalog: $($catIds.Count) tweaks, $(@($cat.profiles).Count) profiles - OK"

# ---- 2. compile the WPF app with all staged files + UI embedded
$buildInfo = Join-Path $gen 'BuildInfo.cs'
"static class BuildInfo { public const string Version = `"$Version`"; }" | Set-Content $buildInfo -Encoding UTF8
$exe  = Join-Path $dist 'UltimateOptimizer.exe'
$ui   = Join-Path $root 'launcher/ui'
$icon = Join-Path $ui 'app.ico'
$resources  = @(Get-ChildItem $stage -File | ForEach-Object { "-resource:$($_.FullName),app/$($_.Name)" })
$resources += Get-ChildItem $ui -File | ForEach-Object { "-resource:$($_.FullName),ui/$($_.Name)" }
$sources = @(Get-ChildItem (Join-Path $root 'launcher') -Filter *.cs | ForEach-Object FullName) + $buildInfo
$libs = 'mscorlib', 'System', 'System.Core', 'System.Xml', 'System.Xaml', 'System.Management', 'System.Web.Extensions', 'System.Drawing', 'Microsoft.CSharp', 'Microsoft.VisualBasic', 'WindowsBase', 'PresentationCore', 'PresentationFramework'
$common = @('-target:winexe', '-platform:anycpu', '-optimize+', '-nologo', "-win32icon:$icon", "-out:$exe")

$csc = $null
if ($env:WINDIR) {
    $csc = 'Framework64', 'Framework' | ForEach-Object { Join-Path $env:WINDIR "Microsoft.NET\$_\v4.0.30319\csc.exe" } |
           Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($csc) {
    $fx = Split-Path $csc
    $refs = foreach ($l in $libs) {
        $p = @("$fx\$l.dll", "$fx\WPF\$l.dll") | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $p) { throw "reference $l.dll not found in $fx" }
        "-r:$p"
    }
    & $csc @common -codepage:65001 "-win32manifest:$(Join-Path $root 'launcher/app.manifest')" @refs @resources @sources
} elseif (Get-Command mcs -ErrorAction SilentlyContinue) {
    # Linux check build: compile against the .NET Framework 4.8 reference assemblies (NuGet:
    # microsoft.netframework.referenceassemblies.net48) -> pass their folder via -RefDir or $env:NET48_REFS
    $refDir = if ($RefDir) { $RefDir } else { $env:NET48_REFS }
    if (-not $refDir -or -not (Test-Path (Join-Path $refDir 'PresentationFramework.dll'))) { throw "set -RefDir to the .NET 4.8 reference assemblies folder" }
    $refs = $libs | ForEach-Object { "-r:" + (Join-Path $refDir "$_.dll") }
    & mcs @common -langversion:5 -nostdlib -noconfig @refs @resources @sources
} else { throw "No C# compiler found (csc.exe from .NET Framework 4 on Windows, or mcs on Linux)" }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "compile failed" }
Copy-Item $exe $stage

# ---- 3. zip (scripts + exe) and checksums
$zip = Join-Path $dist "UltimateOptimizer-v$Version.zip"
Compress-Archive -Path $stage -DestinationPath $zip -Force
$sums = foreach ($f in $exe, $zip) { "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLower(), (Split-Path $f -Leaf) }
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "Built v${Version}:" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object { "  {0,-40} {1,10:N0} bytes" -f $_.Name, $_.Length }
