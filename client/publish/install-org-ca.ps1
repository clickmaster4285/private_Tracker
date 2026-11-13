#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install the Alpha AI Tracker private-network TLS root CA into the Windows trust store.

.DESCRIPTION
  Fleet / production (private LAN HTTPS): every PC must trust the same org root CA that
  signed the API certificate (IP or internal DNS SAN). After this, the desktop client
  and DlpService use normal HTTPS without ALPHA_TLS_INSECURE.

  Normal path: the Windows installer runs this logic automatically at post-install when
  publish\windows\tls\rootCA.pem was baked from repo certs\rootCA.pem. Use this script
  only for lab repair / non-installer machines.

  Also copies the PEM to %ProgramData%\AlphaAITracker\tls\rootCA.pem so the tracker can
  load it as ALPHA_TLS_CA fallback before GPO replication finishes.

.PARAMETER CaPem
  Path to rootCA.pem (mkcert CAROOT or your enterprise root).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install-org-ca.ps1 -CaPem C:\secure\org-rootCA.pem
#>
param(
    [string]$CaPem = ""
)

$ErrorActionPreference = "Stop"

function Resolve-CaPem([string]$hint) {
    if ($hint -and (Test-Path -LiteralPath $hint)) {
        return (Resolve-Path -LiteralPath $hint).Path
    }
    $prog = Join-Path $env:ProgramData "AlphaAITracker\tls\rootCA.pem"
    if (Test-Path -LiteralPath $prog) { return $prog }

    $repo2 = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\certs\rootCA.pem"))
    if (Test-Path -LiteralPath $repo2) { return $repo2 }

    throw "rootCA.pem not found. Pass -CaPem path\to\rootCA.pem"
}

$ca = Resolve-CaPem $CaPem
Write-Host "Using CA: $ca"

$destDir = Join-Path $env:ProgramData "AlphaAITracker\tls"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destPem = Join-Path $destDir "rootCA.pem"
Copy-Item -LiteralPath $ca -Destination $destPem -Force
Write-Host "Copied → $destPem"

# Prefer certutil (handles PEM); falls back to .NET CreateFromPemFile on newer runtimes.
$certutil = Get-Command certutil.exe -ErrorAction SilentlyContinue
if ($certutil) {
    & certutil.exe -addstore -f Root $destPem | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "certutil -addstore Root failed (exit $LASTEXITCODE)"
    }
    Write-Host "Installed CA into LocalMachine\Root via certutil."
}
else {
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($destPem)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
    $store.Open("ReadWrite")
    try {
        $store.Add($cert)
        Write-Host "Installed CA into LocalMachine\Root (thumbprint $($cert.Thumbprint))"
    }
    finally { $store.Close() }
}

Write-Host ""
Write-Host "Done. Set client ALPHA_SERVER_URL=https://YOUR_API_IP:8000 and restart tracker + DlpService."
Write-Host "Optional: ALPHA_TLS_CA=$destPem (auto-discovered under ProgramData if left unset)."
