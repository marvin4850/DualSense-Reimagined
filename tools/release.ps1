# Builds the release zip that is uploaded to Nexus Mods. ASCII only (PowerShell 5.1 reads scripts without BOM as ANSI).
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1
# Output: release\DualSense-Reimagined-v<ModVersion>.zip (+ .sha256). Layout inside the zip (extract into the game folder):
#   G1R\Mods\G1R_DualSenseBridge\...   mod (official ModKit folder)
#   GothicDualSense\GothicDualSense.exe   launcher (no console; windows only on errors)
#   GothicDualSense\bin\DsBridge.exe      bridge, self-contained (no .NET install needed)
#   GothicDualSense\haptics\...           rules + HD bank (16-bit, no preview reels)
#   GothicDualSense\mod\G1R_DualSenseBridge   mod copy, the launcher re-syncs it into the game on every start
#   README.txt
$ErrorActionPreference = 'Stop'
$ds   = Split-Path $PSScriptRoot -Parent            # repository root
$out  = Join-Path $ds 'release'
$stage = Join-Path $out 'stage'
$pkg  = Join-Path $stage 'GothicDualSense'

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Need($ok, $msg) { if (-not $ok) { throw $msg } }

$lp = Get-Content (Join-Path $ds 'Mod\G1R_DualSenseBridge\Script\G1R_DualSenseBridge\DsLp_Lockpick.as') -Raw
Need ($lp -match 'const int ModVersion = (\d+);') 'ModVersion not found in DsLp_Lockpick.as'
$ver = $Matches[1]
Write-Host "mod version v$ver"

Step 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null

Step 'publish DsBridge (self-contained)'
& dotnet publish (Join-Path $ds 'DsBridge\DsBridge.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $pkg 'bin') --nologo -v q
Need ($LASTEXITCODE -eq 0) 'DsBridge publish failed'
Get-ChildItem (Join-Path $pkg 'bin') -Filter *.pdb | Remove-Item

Step 'publish launcher (single file, trimmed)'
$lt = Join-Path $out 'launcher_tmp'
if (Test-Path $lt) { Remove-Item $lt -Recurse -Force }
& dotnet publish (Join-Path $ds 'Launcher\GothicDualSense.csproj') -c Release -r win-x64 --self-contained true -o $lt --nologo -v q `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:DebugType=none
Need ($LASTEXITCODE -eq 0) 'launcher publish failed'
Copy-Item (Join-Path $lt 'GothicDualSense.exe') $pkg
Remove-Item $lt -Recurse -Force

Step 'haptics (no preview, 16-bit bank)'
& python -I (Join-Path $PSScriptRoot 'release_haptics.py') (Join-Path $ds 'haptics') (Join-Path $pkg 'haptics')
Need ($LASTEXITCODE -eq 0) 'haptics copy failed'

Step 'mod'
$modSrc = Join-Path $ds 'Mod\G1R_DualSenseBridge'
foreach ($d in (Join-Path $pkg 'mod\G1R_DualSenseBridge'), (Join-Path $stage 'G1R\Mods\G1R_DualSenseBridge')) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    Copy-Item (Join-Path $modSrc '*') $d -Recurse
    Get-ChildItem $d -Recurse -Include *.bak | Remove-Item
}
Copy-Item (Join-Path $ds 'Launcher\README.txt') $stage

Step 'package self-check (rules-check on the packaged haptics)'
$rc = & (Join-Path $pkg 'bin\DsBridge.exe') rules-check --haptics (Join-Path $pkg 'haptics\haptics.json') 2>&1 | Out-String
$missing = @($rc -split "`n" | Where-Object { $_ -match 'sample missing|ERROR' })
if ($LASTEXITCODE -ne 0 -or $missing.Count -gt 0) { Write-Host $rc; throw 'packaged haptics failed rules-check' }
Write-Host 'rules-check OK, no missing samples'

Step 'zip'
$zip = Join-Path $out "DualSense-Reimagined-v$ver.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ascii "$zip.sha256"
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$unz = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "`n$zip  $mb MB (unpacked $unz MB)`nSHA256 $hash" -ForegroundColor Green
