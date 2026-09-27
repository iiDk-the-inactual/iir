$ErrorActionPreference = 'Stop'$ProgressPreference = 'SilentlyContinue'

try { 
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 
} catch {}

$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.4/BepInEx_win_x64_5.4.23.4.zip'
$LocalDllPath = Join-Path$PSScriptRoot 'ii.Reborn.dll'

function Fail($msg) { 
    Write-Host "`n$msg" -ForegroundColor Red
    Read-Host 'Press Enter to exit'
    exit 1 
}

if (-not (Test-Path $LocalDllPath)) {
    Fail "Missing local plugin file! Please make sure 'ii.Reborn.dll' is in the same directory as this script."
}

Write-Host ''
Write-Host '  ii Reborn - Local Installer' -ForegroundColor Yellow
Write-Host ''

$candidates = @(
    'C:\Program Files (x86)\Steam\steamapps\common\Gorilla Tag',
    'D:\SteamLibrary\steamapps\common\Gorilla Tag',
    'C:\Program Files\Oculus\Software\Software\another-axiom-gorilla-tag',
    'D:\Steam\steamapps\common\Gorilla Tag'
)

$found = @($candidates | Where-Object { Test-Path "$($_)\Gorilla Tag.exe" })

if ($found.Count -eq 0) {
    $gamePath = (Read-Host 'Gorilla Tag directory not found. Enter it manually').Trim('"')
    if (-not (Test-Path $gamePath)) { Fail 'Invalid directory.' }
} elseif ($found.Count -eq 1) {
    $gamePath = $found[0]
} else {
    Write-Host 'Multiple Gorilla Tag installations found:' -ForegroundColor Yellow
    for ($i = 0; $i -lt $found.Count; $i++) {
        Write-Host ('  [{0}] {1}' -f ($i + 1), $found[$i])
    }
    $pick = 0
    while ($true) {
        $answer = (Read-Host "Choose [1-$($found.Count)] (Enter = 1)").Trim()
        if ($answer -eq '') { $pick = 1; break }
        if ([int]::TryParse($answer, [ref]$pick) -and $pick -ge 1 -and $pick -le $found.Count) { break }
        Write-Host 'Invalid choice.' -ForegroundColor Red
    }
    $gamePath = $found[$pick - 1]
}

Write-Host "Game directory: $gamePath`n"

Write-Host 'Downloading BepInEx...' -ForegroundColor Cyan
$zip = Join-Path$env:TEMP 'iireborn-bepinex.zip'

try {
    Invoke-WebRequest -UseBasicParsing -Uri $BepInExUrl -OutFile$zip
    Write-Host 'Extracting BepInEx...' -ForegroundColor Cyan
    
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in$archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }$rel = $entry.FullName -replace '/', '\'$target = $gamePath + '\' +$rel
            $slash =$rel.LastIndexOf('\')
            if ($slash -ge 0) {
                $targetDir =$gamePath + '\' + $rel.Substring(0,$slash)
                if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path$targetDir | Out-Null }
            }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target,$true)
        }
    } finally {
        $archive.Dispose()
    }
} catch {
    if ($env:IIREBORN_ELEVATED -eq '1') { 
        Fail "Failed to download/extract BepInEx ($($_.Exception.Message))" 
    }
    
    Write-Host "BepInEx step failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host 'Relaunching as administrator - accept the UAC prompt!!!' -ForegroundColor Yellow
    
    $child = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    try {
        $env:IIREBORN_ELEVATED = '1'
        Start-Process powershell -Verb RunAs -ArgumentList $child
    } catch {
        Fail 'Administrator access was declined. Re-run the installer and accept the UAC prompt.'
    }
    exit
}

Remove-Item $zip -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$gamePath\BepInEx\config", "$gamePath\BepInEx\plugins" | Out-Null

Get-ChildItem -Path "$gamePath\BepInEx\plugins" -Filter 'ii*.dll' -Recurse -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path "$gamePath\BepInEx\plugins" -Filter 'ii*' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Installing ii Reborn from local file...' -ForegroundColor Cyan
try {
    Copy-Item -Path $LocalDllPath -Destination "$gamePath\BepInEx\plugins\ii.Reborn.dll" -Force
} catch { 
    Fail "Failed to copy the menu ($($_.Exception.Message))" 
}

Write-Host ''
Write-Host 'Congratulations, you now have the menu installed locally!' -ForegroundColor Green
Write-Host 'Launch Gorilla Tag and the menu will load automatically.'
Write-Host ''
Read-Host 'All good, press Enter to exit or close this window'
