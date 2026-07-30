# Publishes one Nullprice tool as a release.
#
#   .\release-app.ps1 -App Ferry -Version 0.2.0
#   .\release-app.ps1 -App Batch -Version 0.2.0 -Push
#
# Without -Push this only builds the artefacts and updates the local catalogue, so the
# store can install the new version from the local feed. With -Push it also creates a
# GitHub release, which requires the gh CLI and a repository for that app.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Ferry', 'Batch')]
    [string]$App,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [switch]$Push
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root "apps\$App\app\Nullprice.$App.App\Nullprice.$App.App.csproj"
$feed = Join-Path $root 'store\feed'
$cataloguePath = Join-Path $root 'store\catalogue.json'

if (-not (Test-Path $project)) {
    Write-Host "No project at $project" -ForegroundColor Red
    exit 1
}

$assetName = "$App-$Version-setup.exe"
$staging = Join-Path $feed "_$App-staging"

Write-Host "Building $App $Version..." -ForegroundColor DarkGray

dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -p:FileVersion=$Version `
    -o $staging -v quiet

if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force -Path $feed | Out-Null
$built = Join-Path $staging "Nullprice.$App.App.exe"
$target = Join-Path $feed $assetName

Move-Item $built $target -Force
Remove-Item $staging -Recurse -Force

$file = Get-Item $target
$hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower()

# A sibling .sha256 file is how the store verifies a GitHub download, since GitHub
# publishes no checksum of its own for release assets.
"$hash  $assetName" | Out-File -FilePath "$target.sha256" -Encoding ascii -NoNewline

Write-Host "  $assetName" -ForegroundColor Green
Write-Host "  $([math]::Round($file.Length / 1MB, 1)) MB   sha256 $hash" -ForegroundColor DarkGray

# ---- update the catalogue -------------------------------------------------

$catalogue = Get-Content $cataloguePath -Raw | ConvertFrom-Json
$entry = $catalogue.apps | Where-Object { $_.id -eq $App.ToLower() }

if (-not $entry) {
    Write-Host "No catalogue entry with id '$($App.ToLower())'." -ForegroundColor Red
    exit 1
}

$entry.status = 'available'
$entry.download.version = $Version
$entry.download.filename = $assetName
$entry.download.url = "./feed/$assetName"
$entry.download.sha256 = $hash
$entry.download.size = $file.Length

$catalogue | ConvertTo-Json -Depth 12 | Set-Content $cataloguePath -Encoding utf8
Write-Host "Catalogue updated." -ForegroundColor Green

# ---- publish --------------------------------------------------------------

if ($Push) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        Write-Host 'gh CLI not found. Install it with: winget install GitHub.cli' -ForegroundColor Yellow
        exit 1
    }

    $repo = $entry.updates.repo
    $owner = $entry.updates.owner

    if ($owner -eq 'REPLACE-ME') {
        Write-Host "Set updates.owner for $App in catalogue.json first." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "Publishing to $owner/$repo..." -ForegroundColor DarkGray
    gh release create "v$Version" $target "$target.sha256" `
        --repo "$owner/$repo" `
        --title "$App $Version" `
        --generate-notes

    if ($LASTEXITCODE -ne 0) { Write-Host 'Publish failed.' -ForegroundColor Red; exit 1 }
    Write-Host "Published $App $Version." -ForegroundColor Green
} else {
    Write-Host 'Not pushed. Re-run with -Push to publish a GitHub release.' -ForegroundColor DarkGray
}
