# Bundle the full VC++ 2015–2022 x64 CRT next to the Windows client AND always
# download vc_redist.x64.exe so Inno can quiet-install the system redistributable
# on clean Win10 Pro PCs (Programs and Features will then show "Microsoft Visual C++ …").
# Microsoft permits app-local redistribution of these CRT binaries with the application.
param(
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($OutDir) -and -not [string]::IsNullOrWhiteSpace($env:ALPHA_VPX_OUTDIR)) {
    $OutDir = $env:ALPHA_VPX_OUTDIR
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    throw "ensure-vpx-native: -OutDir or ALPHA_VPX_OUTDIR is required"
}
$OutDir = $OutDir.Trim().Trim('"').TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

function Find-First([string[]]$candidates) {
    foreach ($c in $candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if (Test-Path -LiteralPath $c) {
            return (Get-Item -LiteralPath $c)
        }
        $hit = @(Get-Item -Path $c -ErrorAction SilentlyContinue)
        if ($hit.Count -gt 0) { return $hit[0] }
    }
    return $null
}

$vsRoots = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022",
    "${env:ProgramFiles}\Microsoft Visual Studio\2019",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019"
) | Where-Object { Test-Path $_ }

$crtDirs = @()
foreach ($root in $vsRoots) {
    $found = Get-ChildItem -Path $root -Directory -Recurse -Filter "Microsoft.VC*.CRT" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\Microsoft\.VC\d+\.CRT$' } |
        Select-Object -ExpandProperty FullName
    # Prefer desktop Redist CRT over OneCore (desktop LoadLibrary path).
    $desktop = @($found | Where-Object { $_ -notmatch '\\onecore\\' })
    $crtDirs += $(if ($desktop.Count -gt 0) { $desktop } else { $found })
}

# Required for ORT / vpxmd; extras copied when present so every VC++ CRT piece ships.
$requiredNames = @("vcruntime140.dll", "msvcp140.dll")
$optionalNames = @(
    "vcruntime140_1.dll",
    "msvcp140_1.dll",
    "msvcp140_atomic_wait.dll",
    "msvcp140_codecvt_ids.dll",
    "concrt140.dll",
    "vccorlib140.dll"
)
$allNames = $requiredNames + $optionalNames

$copiedRequired = @()
foreach ($name in $allNames) {
    $candidates = @()
    foreach ($dir in $crtDirs) {
        $candidates += (Join-Path $dir $name)
    }
    $candidates += (Join-Path $env:WINDIR "System32\$name")

    $src = Find-First $candidates
    if ($null -eq $src) {
        if ($requiredNames -contains $name) {
            Write-Warning "ensure-vpx-native: REQUIRED $name not found (VS Redist or System32)."
        }
        continue
    }

    $dest = Join-Path $OutDir $name
    Copy-Item -LiteralPath $src.FullName -Destination $dest -Force
    Write-Host "  ✓ $name ← $($src.FullName)"
    if ($requiredNames -contains $name) { $copiedRequired += $name }
}

foreach ($need in $requiredNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutDir $need))) {
        throw "ensure-vpx-native: missing required CRT $need in $OutDir — cannot bake Windows Setup."
    }
}

# Always bundle BOTH x64 + x86 system redistributables (Inno quiet-installs both).
# Set ALPHA_BUNDLE_VC_REDIST=0 to skip download when IT already mirrored the exes.
$redistDir = Join-Path $OutDir "redist"
$skipDownload = $env:ALPHA_BUNDLE_VC_REDIST -eq "0"
New-Item -ItemType Directory -Path $redistDir -Force | Out-Null

$redists = @(
    @{ Name = "vc_redist.x64.exe"; Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe" },
    @{ Name = "vc_redist.x86.exe"; Url = "https://aka.ms/vs/17/release/vc_redist.x86.exe" }
)
foreach ($r in $redists) {
    $dest = Join-Path $redistDir $r.Name
    if ((Test-Path -LiteralPath $dest) -and ((Get-Item -LiteralPath $dest).Length -gt 1MB)) {
        Write-Host "  ✓ $($r.Name) (already present)"
        continue
    }
    if ($skipDownload) {
        throw "ensure-vpx-native: missing $dest and ALPHA_BUNDLE_VC_REDIST=0 (no download)."
    }
    Write-Host "  ↓ Downloading $($r.Name) → $dest"
    try {
        Invoke-WebRequest -Uri $r.Url -OutFile $dest -UseBasicParsing
        if (-not (Test-Path -LiteralPath $dest) -or ((Get-Item -LiteralPath $dest).Length -lt 1MB)) {
            throw "download produced empty/small file"
        }
        Write-Host "  ✓ $($r.Name) ($([math]::Round((Get-Item $dest).Length/1MB, 1)) MB)"
    }
    catch {
        throw "ensure-vpx-native: failed to download $($r.Name): $_"
    }
}

foreach ($r in $redists) {
    $dest = Join-Path $redistDir $r.Name
    if (-not (Test-Path -LiteralPath $dest)) {
        throw "ensure-vpx-native: missing $dest — Windows Setup would skip system VC++ install."
    }
}

Write-Host "  ✓ CRT required: $($copiedRequired -join ', '); redist x64+x86 ready"
exit 0
