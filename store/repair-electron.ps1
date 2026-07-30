# Repairs a broken Electron install.
#
# npm's electron postinstall can exit 0 having downloaded the zip but extracted almost
# none of it — the symptom is node_modules/electron/dist containing only `locales`, and
# `npm start` failing with "Electron failed to install correctly". Reinstalling does not
# fix it, because the zip is already cached so the postinstall skips straight to a
# no-op. This script finishes the job from the cached zip.
#
#   .\repair-electron.ps1

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$electron = Join-Path $root 'node_modules\electron'

if (-not (Test-Path $electron)) {
    Write-Host "node_modules/electron is missing. Run 'npm install' first." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $electron 'dist\electron.exe'
if (Test-Path $exe) {
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Electron is already installed ($mb MB). Nothing to do." -ForegroundColor Green
    exit 0
}

$version = (Get-Content (Join-Path $electron 'package.json') -Raw | ConvertFrom-Json).version
Write-Host "Repairing Electron $version…" -ForegroundColor DarkGray

$cache = Join-Path $env:LOCALAPPDATA 'electron\Cache'
$zip = Get-ChildItem $cache -Recurse -Filter "electron-v$version-win32-x64.zip" -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $zip) {
    Write-Host "No cached zip for v$version in $cache." -ForegroundColor Red
    Write-Host "Delete node_modules/electron and run 'npm install' to fetch it." -ForegroundColor Yellow
    exit 1
}

Remove-Item (Join-Path $electron 'dist') -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip.FullName -DestinationPath (Join-Path $electron 'dist') -Force

# install.js normally writes this; getElectronPath() refuses to start without it.
'electron.exe' | Out-File -FilePath (Join-Path $electron 'path.txt') -Encoding ascii -NoNewline

if (Test-Path $exe) {
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Electron $version restored ($mb MB)." -ForegroundColor Green
} else {
    Write-Host 'Extraction completed but electron.exe is still absent.' -ForegroundColor Red
    exit 1
}
