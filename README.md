# WinDbg MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server that gives AI agents complete control over a Windows VM for kernel debugging, reverse engineering, malware analysis, and vulnerability research.

Built in C# (.NET 8), it wraps the Windows Debugger Engine (DbgEng COM), VMware Workstation, Frida, and dbgsrv into **29 MCP tools** that any MCP-compatible LLM client can call.

![Architecture](https://img.shields.io/badge/.NET_8-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Windows-0078D6?logo=windows&logoColor=white)
![VMware](https://img.shields.io/badge/VMware_Workstation-607078?logo=vmware&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

## Why this project?

Other WinDbg MCP servers exist — most are Python wrappers that launch `cdb.exe` or `windbg.exe` as a subprocess and drive it over stdin/stdout. That's easy to prototype but fragile in practice: the child debugger crashes, hangs on modal dialogs, deadlocks its own pipes, or dies mid-session and takes the agent's context with it.

This project takes a different approach:

- **Direct DbgEng COM** — calls the Windows Debugger Engine natively through its COM interface. No subprocess to babysit, no stdout parsing, no hung pipes. Commands execute inside the server process on a dedicated MTA COM thread with an event pump — so the debugger can't drag the whole MCP server down with it.
- **Kernel debugging is the primary use case, not an afterthought** — full KDNET integration: attach to a running kernel, set breakpoints, step, run any WinDbg command while the target is halted, wait for events with hard timeouts, detect BSODs, and pass first-chance exceptions through so Windows keeps running normally. Execution-control commands (`g`/`t`/`p`) are blocked in `kd_execute` so the LLM can't accidentally run away from a breakpoint — it has to use the explicit `kd_continue`/`kd_step` tools, which always return.
- **User-mode is covered too** — Frida (with eternalized hooks for persistent instrumentation across sessions), dbgsrv for noninvasive process inspection with full WinDbg command access, and TTD (Time Travel Debugging) — all behind the same server.
- **VM lifecycle is integrated with the debug session** — snapshot restore cleanly tears down KD/Frida/dbgsrv before reverting the VM; guest commands, file transfer, and process control are all gated on VM power state so the agent never trips on "wrong state" errors. `vm_set_target` lets a running server switch to a different VM at runtime.
- **Designed for LLM agents** — every tool has a hard timeout (nothing blocks forever), every error message explicitly tells the LLM what to do next, and `get_system_state` gives the model a single "where am I?" snapshot on demand. A `StateCoordinator` validates preconditions before every call so the agent gets useful feedback instead of silent failures.

## What Can It Do?

An LLM connected to this server can autonomously:

- **Control a VM** — start, stop, pause, resume, snapshot, restore, screenshot
- **Kernel debug** — connect via KDNET, set breakpoints, step, execute any WinDbg command, wait for events
- **Run commands in the guest** — execute programs, transfer files, list/kill processes
- **User-mode debug** — attach Frida to hook functions, inspect processes via dbgsrv, record TTD traces

All without the LLM ever needing direct access to WinDbg, a terminal, or the VM itself.

## Quick Start

### Prerequisites

| Requirement | Purpose |
|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Build & run the server |
| [VMware Workstation Pro](https://www.vmware.com/products/workstation-pro.html) | VM management (vmrun) |
| Windows Guest VM | Target for debugging |
| KDNET enabled in guest | Kernel debugging (see [Setup](#vm-setup)) |
| [frida-tools](https://frida.re/) *(optional)* | User-mode instrumentation |
| [WinDbg Preview](https://aka.ms/windbg) *(optional)* | Provides dbgsrv.exe for remote user-mode debugging |

### 1. Clone & Build

```bash
git clone https://github.com/memoryforensics1/windbg-mcp.git
cd windbg-mcp
dotnet build src/WinDbgMCP.Server/WinDbgMCP.Server.csproj
```

### 2. Configure *(optional)*

<details>
<summary>Everything in <code>appsettings.json</code> is just a default — the LLM can change VM target, credentials, KDNET key, and more at runtime via <code>vm_set_target</code>. Expand only if you want to set a starting config.</summary>

Copy `src/WinDbgMCP.Server/appsettings.example.json` to `appsettings.json` and edit:

```json
{
  "Vm": {
    "VmxPath": "C:\\path\\to\\your\\vm.vmx",
    "VmrunPath": "C:\\Program Files (x86)\\VMware\\VMware Workstation\\vmrun.exe",
    "HostType": "ws",
    "HostUrl": "",
    "HostUsername": "",
    "HostPassword": "",
    "VmPassword": "",
    "GuestUsername": "YourUser",
    "GuestPassword": "YourPass"
  },
  "KernelDebug": {
    "Transport": "kdnet",
    "Kdnet": {
      "Port": 50000,
      "Key": "your.kdnet.key.here"
    },
    "SymbolPath": "srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols"
  },
  "Guest": {
    "FridaPort": 27042,
    "DbgsrvPort": 5064
  }
}
```

**Remote hypervisors (ESXi / vCenter / shared Workstation):** VM and guest operations can target a VM on another machine. Set `HostType` to `esx`, `vc`, or `ws-shared`, point `HostUrl` at the hypervisor API (e.g. `https://esxi-host/sdk`), and provide `HostUsername`/`HostPassword`. For esx/vc, `VmxPath` is a datastore path like `[datastore1] win10/win10.vmx`. vmrun still runs locally, so VMware Workstation (or VIX) must be installed on this machine. All of this can also be switched at runtime via `vm_set_target`. Note that kernel debugging is independent of this: the KDNET target must be able to reach this host over UDP regardless of where the VM runs.

</details>

### 3. Add to Your MCP Client

**Claude Code** (`.mcp.json` in project root):
```json
{
  "mcpServers": {
    "windbg-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\windbg-mcp\\src\\WinDbgMCP.Server\\WinDbgMCP.Server.csproj"]
    }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "windbg-mcp": {
      "command": "C:\\Program Files\\dotnet\\dotnet.exe",
      "args": ["run", "--project", "C:\\path\\to\\windbg-mcp\\src\\WinDbgMCP.Server\\WinDbgMCP.Server.csproj"]
    }
  }
}
```

### 4. Run

The server starts automatically when your MCP client connects. It communicates over stdio.

```bash
# Or run standalone for testing
dotnet run --project src/WinDbgMCP.Server/WinDbgMCP.Server.csproj
```

## Tool Catalog (29 tools)

### Meta
| Tool | Description |
|---|---|
| `get_system_state` | Full state overview — VM power, KD, guest ops, UMD. Always allowed. |

### VM Tools (8)
| Tool | Description |
|---|---|
| `vm_start` | Power on the VM |
| `vm_stop` | Shut down (graceful or hard) |
| `vm_pause` | Freeze entire VM |
| `vm_resume` | Unpause a paused VM |
| `vm_snapshot_restore` | Restore a named snapshot (debug sessions are cleanly torn down and can reconnect after) |
| `vm_snapshot_list` | List available snapshots |
| `vm_screenshot` | Capture VM display as PNG |
| `vm_set_target` | Switch the active VM target at runtime (VMX path + credentials, optionally a remote ESXi/vCenter host) |

### Kernel Debug Tools (7)
| Tool | Description |
|---|---|
| `kd_connect` | Attach to kernel via KDNET. Target breaks on connect. |
| `kd_disconnect` | Detach from kernel. Resumes target so VM keeps running. |
| `kd_break` | Halt running target (Ctrl+Break) |
| `kd_continue` | Resume target execution |
| `kd_step` | Step one instruction (into or over) |
| `kd_execute` | Run any WinDbg command (`k`, `r`, `lm`, `!process 0 0`, `!analyze -v`, etc.) |
| `kd_wait_for_event` | Wait for breakpoint/exception with timeout. Always returns. |

### Guest Tools (5)
| Tool | Description |
|---|---|
| `guest_run_command` | Execute command in guest OS, capture stdout/stderr |
| `guest_transfer_to_vm` | Copy file from host to guest |
| `guest_transfer_from_vm` | Copy file from guest to host |
| `guest_list_processes` | List running processes with PIDs |
| `guest_kill_process` | Kill a process by PID |

### User-Mode Debug Tools (8)
| Tool | Description |
|---|---|
| `umd_frida_attach` | Attach Frida to a guest process |
| `umd_frida` | Inject JS, eval expressions, list processes, detach |
| `umd_frida_skill` | Frida best practices and API reference for LLMs |
| `umd_dbgsrv_connect` | Connect to remote dbgsrv in guest |
| `umd_dbgsrv_execute` | Attach to PID, run WinDbg commands, detach |
| `umd_dbgsrv_skill` | dbgsrv best practices and WinDbg command reference for LLMs |
| `umd_ttd` | Time Travel Debugging — record, stop, retrieve, list traces |
| `umd_ttd_query` | Query TTD traces *(not yet implemented)* |

## Example Workflows

### Inspect a Running Kernel

```
get_system_state
kd_connect
kd_execute("lm")                    # list loaded modules
kd_execute("!process 0 0")          # list all processes
kd_execute("vertarget")             # target version info
kd_disconnect
```

### Set Breakpoint and Catch It

```
kd_connect
kd_execute("bp nt!NtCreateFile")    # set breakpoint
kd_continue                          # let target run
kd_wait_for_event(30)               # wait up to 30s for hit
kd_execute("k")                     # show call stack
kd_execute("r")                     # show registers
kd_disconnect
```

### Deploy and Debug a Driver

```
guest_transfer_to_vm("MyDriver.sys", "C:\\Windows\\System32\\drivers\\MyDriver.sys")
guest_run_command("sc create MyDrv type= kernel binPath= C:\\Windows\\System32\\drivers\\MyDriver.sys")
guest_run_command("sc start MyDrv")
kd_connect
kd_execute("lm m MyDrv")            # verify driver loaded
kd_execute("bp MyDrv!DriverEntry")
kd_disconnect
```

### Hook a Function with Frida

```
umd_frida_skill                      # read best practices first
umd_frida_attach(processName="target.exe")
umd_frida(action="eval", code="Process.enumerateModules().map(m=>m.name)")
umd_frida(action="inject", code="""
  Interceptor.attach(Module.getExportByName('kernel32.dll','CreateFileW'), {
    onEnter(args) { console.log('CreateFileW: ' + args[0].readUtf16String()); }
  });
  console.log('Hook installed');
""", timeoutSeconds=10)
umd_frida(action="detach")
```

### Inspect a Process with dbgsrv

```
umd_dbgsrv_skill                     # read best practices first
guest_run_command("start /b C:\\Tools\\DbgSrv\\dbgsrv.exe -t tcp:port=5064")
umd_dbgsrv_connect(vmIpAddress="192.168.x.x")
umd_dbgsrv_execute(action="attach", argument="<PID>")
umd_dbgsrv_execute(action="command", argument="lm")
umd_dbgsrv_execute(action="command", argument="!peb")
umd_dbgsrv_execute(action="command", argument="~*k")
umd_dbgsrv_execute(action="detach")
umd_dbgsrv_execute(action="disconnect")
```

### Crash Analysis

```
kd_connect                           # connect after BSOD
kd_execute("!analyze -v")           # automated crash analysis
kd_execute("k")                     # faulting stack
kd_execute("r")                     # registers at crash
kd_execute(".trap")                 # switch to trap frame
kd_disconnect
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      MCP Client (LLM)                       │
│                  Claude Code / Claude Desktop                │
└──────────────────────────┬──────────────────────────────────┘
                           │ stdio (JSON-RPC)
┌──────────────────────────▼──────────────────────────────────┐
│                    WinDbgMCP.Server                          │
│                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │  VM Tools    │  │  KD Tools    │  │  Guest Tools     │  │
│  │  (vmrun)     │  │  (DbgEng)    │  │  (vmrun guest)   │  │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────────┘  │
│         │                 │                  │               │
│  ┌──────▼───────┐  ┌──────▼───────┐  ┌──────▼───────────┐  │
│  │ VmwareManager│  │ DbgEngManager│  │ GuestExecManager │  │
│  └──────────────┘  └──────┬───────┘  └──────────────────┘  │
│                           │                                  │
│  ┌────────────────────────▼─────────────────────────────┐   │
│  │              StateCoordinator                         │   │
│  │   (precondition gate — validates every tool call)     │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │ UMD: Frida   │  │ UMD: dbgsrv  │  │ UMD: TTD         │  │
│  │ (frida CLI)  │  │ (DbgEng COM) │  │ (TTD.exe)        │  │
│  └──────────────┘  └──────────────┘  └──────────────────┘  │
└──────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────────────────────────────────────────────────────┐
│                   Windows Guest VM                           │
│              (VMware Workstation Pro)                         │
│                                                              │
│  KDNET (:50000)  │  frida-server (:27042)  │  dbgsrv (:5064)│
└─────────────────────────────────────────────────────────────┘
```

### Key Design Principles

1. **Every tool validates preconditions** — `StateCoordinator` checks VM power, KD state, guest ops availability before any operation executes
2. **Every operation has a timeout** — no blocking calls, ever. The LLM never hangs.
3. **Error messages are prompts** — every error tells the LLM exactly what to do next
4. **Execution-control commands are blocked** — `g`, `t`, `p` are blocked in `kd_execute`; use `kd_continue`/`kd_step` instead
5. **DbgEng COM thread affinity** — all COM calls marshaled to a dedicated MTA thread with an event pump
6. **BSOD detection** — bugchecks are detected and handled differently from normal breakpoints
7. **Snapshot restore resets everything** — KD, Frida, dbgsrv sessions are all cleaned up

## VM Setup

### Enable KDNET (Kernel Debugging)

In the guest VM (elevated cmd):

```cmd
bcdedit /debug on
bcdedit /dbgsettings net hostip:<HOST_IP> port:50000 key:<YOUR_KEY>
shutdown /r /t 0
```

Generate a key with `kdnet.exe` from the Windows SDK, or use any dotted-quad format key.

### Install Frida Server (Optional)

On the **host**:
```bash
pip install frida-tools
```

Download `frida-server-<version>-windows-x86_64.exe` from [Frida releases](https://github.com/frida/frida/releases), deploy to guest as `C:\Tools\frida-server.exe`, and run:
```cmd
frida-server.exe -l 0.0.0.0:27042
```

### Install dbgsrv (Optional)

Copy `dbgsrv.exe`, `dbgeng.dll`, and `dbghelp.dll` from WinDbg Preview (or Windows SDK) to the guest, then run:
```cmd
dbgsrv.exe -t tcp:port=5064
```

### Firewall

The host firewall must allow:
- UDP port 50000 inbound (KDNET)
- TCP port 27042 outbound (Frida)
- TCP port 5064 outbound (dbgsrv)

## Project Structure

```
src/WinDbgMCP.Server/
├── Program.cs                    # Entry point, MCP server setup, DI
├── appsettings.json              # Configuration (VM creds, KDNET, timeouts)
├── Configuration/
│   └── ServerConfig.cs           # Typed configuration model
├── State/
│   ├── SystemState.cs            # State model + enums
│   ├── StateCoordinator.cs       # Precondition gate (heart of system)
│   ├── ErrorMessages.cs          # LLM-friendly error catalog
│   └── ToolResult.cs             # Result type
├── Vmware/
│   └── VmwareManager.cs          # vmrun wrapper
├── KernelDebug/
│   ├── DbgEngThread.cs           # Dedicated MTA thread for COM
│   ├── DbgEngManager.cs          # Kernel debug session manager
│   ├── DebugEventCallbacks.cs    # Breakpoint/exception/module events
│   ├── OutputCapture.cs          # Command output capture
│   └── Interop/                  # P/Invoke, constants
├── Guest/
│   └── GuestExecManager.cs       # Guest command execution + file transfer
├── UserModeDebug/
│   ├── FridaManager.cs           # Frida CLI wrapper
│   ├── DbgsrvManager.cs          # Remote user-mode debugging via dbgsrv
│   └── TtdManager.cs             # Time Travel Debugging
└── Tools/
    ├── VmTools.cs                # vm_* tools
    ├── KernelDebugTools.cs       # kd_* tools
    ├── GuestTools.cs             # guest_* tools
    ├── UserModeDebugTools.cs     # umd_* tools
    └── MetaTools.cs              # get_system_state

src/WinDbgMCP.Tests/              # Unit tests (126 tests)
```

## Running Tests

```bash
dotnet test src/WinDbgMCP.Tests/WinDbgMCP.Tests.csproj
```

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 8 (C#) |
| MCP SDK | [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) 0.1.0-preview.12 |
| DbgEng | Native COM interop (`dbgeng.dll`) |
| VM Control | VMware vmrun CLI |
| User-Mode Hooking | Frida (frida-tools Python CLI) |
| Remote Debugging | dbgsrv.exe (WinDbg component) |
| TTD | TTD.exe (Time Travel Debugging) |

## License

MIT
