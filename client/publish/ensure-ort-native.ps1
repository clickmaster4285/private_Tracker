# Ensure CPU OnnxRuntime natives sit next to client.exe for face soft-lock.
# Required: onnxruntime.dll + onnxruntime_providers_shared.dll
# Removes DirectML.dll leftovers (2.4.0 DirectML bake) from the publish/output dir.
param(
    [Parameter(Mandatory = $false)]
    [string]$OutDir = $env:ALPHA_ORT_OUTDIR
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    Write-Error "usage: ensure-ort-native.ps1 -OutDir <dir> (or set ALPHA_ORT_OUTDIR)"
    exit 1
}

$OutDir = $OutDir.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
    Write-Error "ensure-ort-native: publish dir missing: $OutDir"
    exit 1
}

function Find-NuGetNative([string]$name) {
    $pkgRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.ml.onnxruntime"
    if (-not (Test-Path $pkgRoot)) { return $null }
    $hits = Get-ChildItem -Path $pkgRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\runtimes\\win-x64\\native\\' } |
        Sort-Object FullName -Descending
    if ($hits) { return $hits[0].FullName }
    return $null
}

function Ensure-File([string]$name) {
    $dest = Join-Path $OutDir $name
    if (Test-Path -LiteralPath $dest) { return $dest }

    $candidates = @(
        (Join-Path $OutDir "runtimes\win-x64\native\$name")
        (Join-Path $OutDir "win-x64\$name")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) {
            Copy-Item -LiteralPath $c -Destination $dest -Force
            Write-Host "  copied $name from $c"
            return $dest
        }
    }

    $nuget = Find-NuGetNative $name
    if ($nuget) {
        Copy-Item -LiteralPath $nuget -Destination $dest -Force
        Write-Host "  copied $name from NuGet cache"
        return $dest
    }

    return $null
}

# Strip DirectML leftovers so upgrades never leave a DirectML-linked ORT dependency.
$dml = Join-Path $OutDir "DirectML.dll"
if (Test-Path -LiteralPath $dml) {
    Remove-Item -LiteralPath $dml -Force
    Write-Host "  removed DirectML.dll (CPU ORT only)"
}

$ort = Ensure-File "onnxruntime.dll"
$shared = Ensure-File "onnxruntime_providers_shared.dll"

if (-not $ort) {
    Write-Error "ensure-ort-native: onnxruntime.dll missing under $OutDir (and could not copy from runtimes/NuGet)."
    exit 1
}
if (-not $shared) {
    Write-Error "ensure-ort-native: onnxruntime_providers_shared.dll missing under $OutDir (required beside onnxruntime.dll)."
    exit 1
}

$ortSize = (Get-Item -LiteralPath $ort).Length
$sharedSize = (Get-Item -LiteralPath $shared).Length
Write-Host ("  OK onnxruntime.dll ({0:N0} bytes)" -f $ortSize)
Write-Host ("  OK onnxruntime_providers_shared.dll ({0:N0} bytes)" -f $sharedSize)
exit 0
