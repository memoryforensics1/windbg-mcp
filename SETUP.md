# Setup Guide

This document explains what the WinDbg MCP server depends on, **what installs automatically vs. what you must provide**, and how to use `scripts/setup.ps1` to bootstrap a fresh machine.

## TL;DR

```powershell
# From the repo root, in PowerShell:
pwsh -File scripts/setup.ps1          # build + install/stage everything possible
pwsh -File scripts/setup.ps1 -CheckOnly   # just diagnose, change nothing
```

The script is idempotent and non-destructive. Re-run it any time.

## What's bundled vs. what isn't

Only the **.NET/NuGet** layer is self-installing. `dotnet build` auto-restores the NuGet packages (`ClrDebug`, `Microsoft.Extensions.*`, `ModelContextProtocol`) — nothing to do there beyond having the .NET 8 SDK. Everything else is an **external tool the server shells out to**, and none of it ships in this repo.

| Dependency | Needed for | Bundled? | How `setup.ps1` handles it |
|---|---|---|---|
| **.NET 8 SDK** | build & run | No (prereq) | Detected; link printed if missing |
| **NuGet packages** | the server itself | **Yes** — auto-restored | Restored during build |
| **frida-tools** (`frida`, `frida-ps` CLIs) | `umd_frida*` (host side) | No | **Installed via `pip install --user frida-tools`** |
| **frida-server.exe** | Frida agent (runs in guest) | No | **Downloaded (version-matched) into `guest-tools\`** |
| **vmrun.exe** | `vm_*` / `guest_*` | No | Detected; VMware Workstation install required |
| **cdb.exe** (WDK debuggers) | `umd_dbgsrv_*` | No | Detected; install hint printed |
| **dbgeng.dll** | kernel debugging (`kd_*`) | Ships with Windows | Used via P/Invoke; WDK version recommended for symbols |
| **dbgsrv.exe** | dbgsrv (runs in guest) | No | Reminder printed; stage into guest manually |
| **TTD.exe / tttracer** | `umd_ttd` (runs in guest) | No | Reminder printed; stage into guest manually |

**Bottom line:** installing on a new machine does **not** automatically pull in Frida, VMware, or the WDK. `setup.ps1` automates the two things that *can* be automated (frida-tools on the host, frida-server for the guest) and clearly reports the rest.

## Host prerequisites you must install yourself

1. **.NET 8 SDK** — <https://dotnet.microsoft.com/download/dotnet/8.0>. Required to build and run.
2. **VMware Workstation Pro** — provides `vmrun.exe`. Required for all `vm_*` and `guest_*` tools. Without it, the server runs in **EXTERNAL-TARGET mode**: VM/guest tools are disabled but kernel debugging over KDNET still works (see [PR #1](https://github.com/memoryforensics1/windbg-mcp/pull/1)).
3. **Python 3.10+** — only needed for the user-mode Frida tools (`umd_frida*`). `setup.ps1` uses it to install frida-tools and to decompress the downloaded frida-server.
4. **Debugging Tools for Windows (cdb.exe)** — from the Windows SDK/WDK or WinDbg. Only needed for `umd_dbgsrv_*`. The server searches:
   - `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe`
   - `C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe`
   - `C:\Debuggers\cdb.exe`

Where the host looks for the `frida` CLI (auto-resolved by `FridaManager`): `%ProgramFiles%\Python31x\Scripts\`, `%LocalAppData%\Programs\Python\Python312\Scripts\`, then `PATH`. If you install Python elsewhere, make sure its `Scripts` directory is on `PATH`.

## Guest-VM components (inside the target VM)

These run **inside the Windows VM being debugged** and therefore cannot be bundled with the host server. `setup.ps1` stages frida-server into `guest-tools\` for you; transfer it with `guest_transfer_to_vm` once the VM is running. The server expects these known paths:

| File | Guest path | Source |
|---|---|---|
| `frida-server.exe` | `C:\Tools\frida-server.exe` (port 27042) | Staged by `setup.ps1` into `guest-tools\` |
| `dbgsrv.exe` + `dbgeng.dll` + `dbghelp.dll` | `C:\Tools\DbgSrv\` (port 5064) | WDK / WinDbg Preview |
| `TTD.exe` | `C:\Tools\TTD\TTD.exe` | WinDbg Preview / WDK |

Start frida-server in the guest, then verify: `guest_run_command("tasklist /FI \"IMAGENAME eq frida-server.exe\"")`.
Start dbgsrv when needed: `guest_run_command("start /b C:\Tools\DbgSrv\dbgsrv.exe -t tcp:port=5064")`.

Also required in the guest: **KDNET enabled** (`bcdedit /dbgsettings net ...`) for kernel debugging — see the README's VM Setup section.

## `scripts/setup.ps1` options

| Flag | Effect |
|---|---|
| *(none)* | Build, install frida-tools, stage frida-server, create `appsettings.json`, run all checks |
| `-CheckOnly` | Diagnose only — no installs, downloads, or build. Prints a status report |
| `-SkipBuild` | Skip `dotnet build` (tooling only) |
| `-SkipFrida` | Skip frida-tools install and frida-server download |
| `-GuestArch <x86_64\|x86\|arm64>` | Guest OS architecture for the frida-server download (default `x86_64`) |

The script ends with a summary table and exits non-zero only if a **blocking** prerequisite (.NET 8 / build) is missing. Missing optional components produce `WARN` rows, not failures.

## After setup

1. Edit `src/WinDbgMCP.Server/appsettings.json` (created from the example) — set `VmxPath`, guest credentials, and KDNET key. This file is git-ignored because it holds secrets.
2. Run the server: `dotnet run --project src/WinDbgMCP.Server/WinDbgMCP.Server.csproj`
3. Point your MCP client at it (see README → MCP Client Configuration).
