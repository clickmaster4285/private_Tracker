# Bundle VC++ CRT DLLs next to the Windows client so vpxmd.dll (SIPSorceryMedia.Encoders)
# can LoadLibrary on machines that do not have the system VC++ 2015-2022 redistributable.
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
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022"
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

$names = @("vcruntime140.dll", "vcruntime140_1.dll")
$copied = 0
foreach ($name in $names) {
    $candidates = @()
    foreach ($dir in $crtDirs) {
        $candidates += (Join-Path $dir $name)
    }
    $candidates += (Join-Path $env:WINDIR "System32\$name")
    $candidates += (Join-Path $env:WINDIR "System32\$($name.ToUpper())")

    $src = Find-First $candidates
    if ($null -eq $src) {
        Write-Warning "ensure-vpx-native: $name not found (VS Redist or System32)."
        continue
    }

    $dest = Join-Path $OutDir $name
    Copy-Item -LiteralPath $src.FullName -Destination $dest -Force
    Write-Host "  ✓ $name ← $($src.FullName)"
    $copied++
}

if ($copied -eq 0) {
    Write-Warning "ensure-vpx-native: no CRT DLLs copied into $OutDir — live stream may fail without VC++ redist."
    exit 0
}

# Optional full redistributable for the Inno installer [Run] step.
$redistDir = Join-Path $OutDir "redist"
$redistExe = Join-Path $redistDir "vc_redist.x64.exe"
if ($env:ALPHA_BUNDLE_VC_REDIST -eq "1" -and -not (Test-Path -LiteralPath $redistExe)) {
    New-Item -ItemType Directory -Path $redistDir -Force | Out-Null
    $url = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
    Write-Host "  ↓ Downloading VC++ redistributable → $redistExe"
    try {
        Invoke-WebRequest -Uri $url -OutFile $redistExe -UseBasicParsing
        Write-Host "  ✓ vc_redist.x64.exe"
    }
    catch {
        Write-Warning "ensure-vpx-native: failed to download vc_redist.x64.exe: $_"
    }
}

exit 0
