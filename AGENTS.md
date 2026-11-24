# AGENTS.md — T3Lab Revit MCP

**Asked to install / connect Revit for the user?** Follow [INSTALL_FOR_AI.md](INSTALL_FOR_AI.md). Do not build from source
for that: the installer downloads the released add-in.

## Repository (for agents changing the code)

| Part | Path | Notes |
|---|---|---|
| Loader (the DLL Revit loads) | `src/T3LabRevit.Loader/` | `IExternalApplication`, ribbon tab **T3Lab MCP**, loads Core from bytes. Keep it small: Revit locks it until restart. |
| Core (server + tools) | `src/T3LabRevit.Core/` | TCP 127.0.0.1:48910-48918, one JSON object per line. Tools in `Tools/*.cs`, each `Registry.Add(...)`. |
| Python engine (`send_code_to_revit`) | `src/T3LabRevit.Scripting/` | IronPython 3.4, loaded by reflection from `addin/<year>/scripting` (own AssemblyLoadContext on .NET 8+); stdlib in `%APPDATA%\T3LabRevitMCP\pylib`. Never reference it from Core. |
| Agent Cursors | `src/T3LabRevit.Core/AgentCursors.cs`, `CursorUi.cs` | Records calls on the socket thread; WPF bar + overlay on their own STA thread; never touch the Revit API there. |
| Bridge (MCP stdio) | `bridge/t3lab_revit_bridge.py` | Standard library only, Python 3.8+, runs with `-I`. |
| Installer | `install.ps1`, `Install.cmd`, `scripts/register_mcp.py` | PowerShell 5.1 compatible, ASCII only, no admin. |

Rules:
- Revit 2022–2027 from one source: `dotnet build src/<Project>/<Project>.csproj -c Release -p:RevitYear=<year>`
  (2022–2024 net48, 2025–2026 net8, 2027 net10; Revit API from the Nice3point NuGet reference packages). Element ids go
  through `Id.V` / `Id.Make` (`Rv.cs`), never `IntegerValue` / `Value` directly.
- All tool arguments and results are mm, m², m³, degrees. A schema string must not contain `'` or `"` inside a
  description (`Json.Schema` turns `'` into `"`): `python scripts/gen_tools_snapshot.py --check` catches it.
- After adding or changing a tool: `python scripts/gen_tools_snapshot.py` (the bridge lists the snapshot while Revit is closed).
- Gates (CI runs them all): build every year (Core, Loader, Scripting) with `-warnaserror`, `python -I scripts/gen_tools_snapshot.py --check`,
  `python -I tests/smoke_bridge.py`, `python -I tests/test_register.py`, and the Windows install job.
- Nothing here is verified inside Revit by CI. Say "NEEDS VERIFICATION in Revit" for behaviour you could not run.
