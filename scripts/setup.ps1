<#
.SYNOPSIS
    One-shot setup / health-check for the WinDbg MCP server and its external dependencies.

.DESCRIPTION
    "Bundle what's possible" bootstrapper. It automates every prerequisite that
    can legally and practically be automated, and clearly reports the ones that
    must be installed by hand (VMware, WDK debuggers) or staged into the guest VM.

    What it does:
      * Verifies the .NET 8 SDK, then restores + builds the server (NuGet packages
        are the only truly "bundled" dependency - they auto-restore on build).
      * Installs frida-tools on the HOST via pip (the frida / frida-ps CLIs the
        server shells out to for umd_frida*).
      * Downloads the matching frida-server.exe for the GUEST into .\guest-tools\
        so you can transfer it to C:\Tools\frida-server.exe inside the VM.
      * Detects vmrun.exe (VMware), cdb.exe (WDK debuggers, for umd_dbgsrv_*), and
        TTD, and tells you exactly what's missing and how to get it.
      * Creates appsettings.json from the example if it doesn't exist yet.
      * Prints a summary table (a "doctor" report).

    Nothing here is destructive. Re-run it any time; it is idempotent.

.PARAMETER CheckOnly
    Diagnose only - no installs, no downloads, no build. Just report status.

.PARAMETER SkipBuild
    Skip the dotnet restore/build step (useful when you only want the tooling).

.PARAMETER SkipFrida
    Skip installing frida-tools and downloading frida-server.

.PARAMETER GuestArch
    Architecture of the GUEST OS for the frida-server download. Default x86_64.
    Valid: x86_64, x86, arm64.

.EXAMPLE
    pwsh -File scripts/setup.ps1
        Full setup: build, install frida-tools, stage frida-server, run checks.

.EXAMPLE
    pwsh -File scripts/setup.ps1 -CheckOnly
        Health check only. Prints what's present and what's missing.
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$SkipBuild,
    [switch]$SkipFrida,
    [ValidateSet('x86_64', 'x86', 'arm64')]
    [string]$GuestArch = 'x86_64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Paths -------------------------------------------------------------------
$RepoRoot     = Split-Path -Parent $PSScriptRoot
$ServerDir    = Join-Path $RepoRoot 'src\WinDbgMCP.Server'
$Csproj       = Join-Path $ServerDir 'WinDbgMCP.Server.csproj'
$AppSettings  = Join-Path $ServerDir 'appsettings.json'
$ExampleSettings = Join-Path $ServerDir 'appsettings.example.json'
$GuestToolsDir = Join-Path $RepoRoot 'guest-tools'

# --- Reporting ---------------------------------------------------------------
$script:Report = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string]$Component, [ValidateSet('OK', 'WARN', 'FAIL', 'INFO')][string]$Status, [string]$Detail)
    $script:Report.Add([pscustomobject]@{ Component = $Component; Status = $Status; Detail = $Detail })
}
function Write-Head { param([string]$m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "  [ OK ] $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Write-Bad  { param([string]$m) Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Write-Info { param([string]$m) Write-Host "  [INFO] $m" -ForegroundColor Gray }

function Get-CommandPath {
    param([string]$Name)
    $c = Get-Command $Name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

Write-Host "WinDbg MCP Server - setup / doctor" -ForegroundColor White
Write-Host "Repo: $RepoRoot"
if ($CheckOnly) { Write-Host "Mode: CHECK ONLY (no changes will be made)" -ForegroundColor Yellow }

# --- 1. .NET SDK -------------------------------------------------------------
Write-Head ".NET 8 SDK"
$dotnet = Get-CommandPath 'dotnet'
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
}
if ($dotnet) {
    $sdks = & $dotnet --list-sdks 2>$null
    $has8 = $sdks | Where-Object { $_ -match '^8\.' }
    if ($has8) {
        Write-Ok "dotnet found: $dotnet (has .NET 8 SDK)"
        Add-Result '.NET 8 SDK' 'OK' $dotnet
    } else {
        Write-Warn "dotnet found but no .NET 8 SDK installed. Installed: $($sdks -join ', ')"
        Add-Result '.NET 8 SDK' 'WARN' 'dotnet present, .NET 8 SDK missing'
    }
} else {
    Write-Bad "dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    Add-Result '.NET 8 SDK' 'FAIL' 'dotnet not on PATH'
}

# --- 2. Build (restores NuGet packages) --------------------------------------
Write-Head "Build server (restores NuGet - the only bundled dependency)"
if ($CheckOnly -or $SkipBuild) {
    Write-Info "Skipped (CheckOnly/SkipBuild)."
    Add-Result 'Build' 'INFO' 'skipped'
} elseif (-not $dotnet) {
    Write-Bad "Cannot build without dotnet."
    Add-Result 'Build' 'FAIL' 'no dotnet'
} else {
    Write-Info "dotnet build $Csproj"
    & $dotnet build $Csproj -c Release --nologo
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Build succeeded (NuGet packages restored)."
        Add-Result 'Build' 'OK' 'restored + built'
    } else {
        Write-Bad "Build failed (exit $LASTEXITCODE)."
        Add-Result 'Build' 'FAIL' "exit $LASTEXITCODE"
    }
}

# --- 3. Python + frida-tools (HOST) ------------------------------------------
Write-Head "Frida host tooling (frida-tools via pip)"
$python = Get-CommandPath 'python'
if (-not $python) { $python = Get-CommandPath 'py' }
$fridaCli = Get-CommandPath 'frida'
$fridaVersion = $null

if ($SkipFrida) {
    Write-Info "Skipped (-SkipFrida)."
    Add-Result 'frida-tools (host)' 'INFO' 'skipped'
} elseif (-not $python) {
    Write-Warn "Python not found. Install Python 3.10+ from https://python.org, then re-run. (Only needed for umd_frida*.)"
    Add-Result 'frida-tools (host)' 'WARN' 'python missing'
} else {
    if ($fridaCli -and -not $CheckOnly) {
        Write-Info "frida CLI already present: $fridaCli"
    }
    if (-not $fridaCli -and -not $CheckOnly) {
        Write-Info "Installing frida-tools (pip install --user frida-tools)..."
        & $python -m pip install --user --upgrade frida-tools
        if ($LASTEXITCODE -ne 0) { Write-Warn "pip install frida-tools failed; user-mode Frida tools will be unavailable." }
        $fridaCli = Get-CommandPath 'frida'
    }
    if ($fridaCli) {
        try { $fridaVersion = (& $fridaCli --version 2>$null).Trim() } catch { $fridaVersion = $null }
        Write-Ok "frida CLI: $fridaCli (v$fridaVersion)"
        Add-Result 'frida-tools (host)' 'OK' "v$fridaVersion"
    } elseif ($CheckOnly) {
        Write-Warn "frida CLI not found (run without -CheckOnly to install)."
        Add-Result 'frida-tools (host)' 'WARN' 'not installed'
    } else {
        Write-Warn "frida CLI still not found after install. Check that Python Scripts dir is on PATH."
        Add-Result 'frida-tools (host)' 'WARN' 'install did not expose CLI'
    }
}

# --- 4. frida-server for the GUEST -------------------------------------------
Write-Head "Frida guest agent (frida-server.exe -> guest-tools\)"
if ($SkipFrida) {
    Write-Info "Skipped (-SkipFrida)."
    Add-Result 'frida-server (guest)' 'INFO' 'skipped'
} elseif (-not $fridaVersion) {
    Write-Warn "Unknown frida version (host frida-tools not installed) - cannot match frida-server. Install frida-tools first."
    Add-Result 'frida-server (guest)' 'WARN' 'no version to match'
} else {
    $stagedServer = Join-Path $GuestToolsDir 'frida-server.exe'
    if (Test-Path $stagedServer) {
        Write-Ok "Already staged: $stagedServer"
        Add-Result 'frida-server (guest)' 'OK' 'staged'
    } elseif ($CheckOnly) {
        Write-Warn "Not staged yet (run without -CheckOnly to download frida-server v$fridaVersion)."
        Add-Result 'frida-server (guest)' 'WARN' 'not staged'
    } else {
        if (-not (Test-Path $GuestToolsDir)) { New-Item -ItemType Directory -Path $GuestToolsDir | Out-Null }
        $xzName = "frida-server-$fridaVersion-windows-$GuestArch.exe.xz"
        $url = "https://github.com/frida/frida/releases/download/$fridaVersion/$xzName"
        $xzPath = Join-Path $GuestToolsDir $xzName
        Write-Info "Downloading $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $xzPath -UseBasicParsing
            # Decompress .xz using Python's lzma (Python is guaranteed present - frida-tools needs it).
            Write-Info "Decompressing (python lzma) -> $stagedServer"
            $py = @"
import lzma, shutil, sys
with lzma.open(sys.argv[1]) as fin, open(sys.argv[2], 'wb') as fout:
    shutil.copyfileobj(fin, fout)
"@
            $tmpPy = Join-Path $GuestToolsDir '_unxz.py'
            Set-Content -Path $tmpPy -Value $py -Encoding utf8
            & $python $tmpPy $xzPath $stagedServer
            Remove-Item $tmpPy -Force -ErrorAction SilentlyContinue
            Remove-Item $xzPath -Force -ErrorAction SilentlyContinue
            if (Test-Path $stagedServer) {
                Write-Ok "Staged frida-server v$fridaVersion -> $stagedServer"
                Write-Info "Transfer it into the guest as C:\Tools\frida-server.exe (guest_transfer_to_vm), then run it."
                Add-Result 'frida-server (guest)' 'OK' "v$fridaVersion staged"
            } else {
                Write-Warn "Decompression produced no file. Extract $xzName manually."
                Add-Result 'frida-server (guest)' 'WARN' 'decompress failed'
            }
        } catch {
            Write-Warn "Download/stage failed: $($_.Exception.Message)"
            Write-Info "Manually grab frida-server-$fridaVersion-windows-$GuestArch.exe.xz from github.com/frida/frida/releases"
            Add-Result 'frida-server (guest)' 'WARN' 'download failed'
        }
    }
}

# --- 5. VMware vmrun (HOST) --------------------------------------------------
Write-Head "VMware vmrun (VM & guest tools)"
$vmrunCandidates = @(
    'C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe',
    'C:\Program Files\VMware\VMware Workstation\vmrun.exe',
    'C:\Program Files (x86)\VMware\VMware VIX\vmrun.exe'
)
$vmrun = $vmrunCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vmrun) { $vmrun = Get-CommandPath 'vmrun' }
if ($vmrun) {
    Write-Ok "vmrun found: $vmrun"
    Add-Result 'vmrun (VMware)' 'OK' $vmrun
} else {
    Write-Warn "vmrun not found. Install VMware Workstation Pro for vm_*/guest_* tools."
    Write-Info "Without it the server runs in EXTERNAL-TARGET mode: kernel debugging over KDNET still works."
    Add-Result 'vmrun (VMware)' 'WARN' 'not installed (KDNET-only mode)'
}

# --- 6. cdb.exe / WDK debuggers (HOST) ---------------------------------------
Write-Head "WDK debuggers (cdb.exe - for umd_dbgsrv_*)"
$cdbCandidates = @(
    'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe',
    'C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe',
    'C:\Debuggers\cdb.exe'
)
$cdb = $cdbCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($cdb) {
    Write-Ok "cdb.exe found: $cdb"
    Add-Result 'cdb (WDK debuggers)' 'OK' $cdb
} else {
    Write-Warn "cdb.exe not found. dbgsrv-based user-mode debugging (umd_dbgsrv_*) will be unavailable."
    $winget = Get-CommandPath 'winget'
    if ($winget) {
        Write-Info "Install the Debugging Tools for Windows: winget install Microsoft.WinDbg  (or the Windows SDK 'Debugging Tools' feature)."
    } else {
        Write-Info "Install the Windows SDK 'Debugging Tools for Windows': https://aka.ms/windbg"
    }
    Add-Result 'cdb (WDK debuggers)' 'WARN' 'not installed'
}

# --- 7. appsettings.json -----------------------------------------------------
Write-Head "Configuration (appsettings.json)"
if (Test-Path $AppSettings) {
    Write-Ok "appsettings.json present."
    Add-Result 'appsettings.json' 'OK' 'present'
} elseif ($CheckOnly) {
    Write-Warn "appsettings.json missing (run without -CheckOnly to create from example)."
    Add-Result 'appsettings.json' 'WARN' 'missing'
} elseif (Test-Path $ExampleSettings) {
    Copy-Item $ExampleSettings $AppSettings
    Write-Ok "Created appsettings.json from appsettings.example.json - edit VM path & credentials."
    Add-Result 'appsettings.json' 'OK' 'created from example'
} else {
    Write-Warn "appsettings.example.json not found; cannot create appsettings.json."
    Add-Result 'appsettings.json' 'WARN' 'no example to copy'
}

# --- Guest-side reminder -----------------------------------------------------
Write-Head "Guest-VM components (must live INSIDE the VM - cannot be bundled here)"
Write-Info "frida-server.exe  -> C:\Tools\frida-server.exe        (staged in .\guest-tools if step 4 succeeded)"
Write-Info "dbgsrv.exe (+dbgeng.dll, dbghelp.dll) -> C:\Tools\DbgSrv\   (from the WDK / WinDbg Preview)"
Write-Info "TTD.exe          -> C:\Tools\TTD\TTD.exe               (from WinDbg Preview or the WDK)"
Write-Info "Transfer them with guest_transfer_to_vm once the VM is running."

# --- Summary -----------------------------------------------------------------
Write-Head "Summary"
$script:Report | Format-Table -AutoSize Component, Status, Detail | Out-Host

$fails = @($script:Report | Where-Object { $_.Status -eq 'FAIL' })
$warns = @($script:Report | Where-Object { $_.Status -eq 'WARN' })
if ($fails.Count -gt 0) {
    Write-Host "$($fails.Count) blocking issue(s). The server needs .NET 8 + a successful build at minimum." -ForegroundColor Red
    exit 1
} elseif ($warns.Count -gt 0) {
    Write-Host "Core server is ready. $($warns.Count) optional component(s) missing - see WARN rows above." -ForegroundColor Yellow
    exit 0
} else {
    Write-Host "All checks passed." -ForegroundColor Green
    exit 0
}
