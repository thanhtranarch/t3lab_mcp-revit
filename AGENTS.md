# AGENTS.md — T3Lab Revit MCP

**Asked to install / connect Revit for the user?** Follow [INSTALL_FOR_AI.md](INSTALL_FOR_AI.md). Do not build from source
for that: the installer downloads the released add-in.

## Repository (for agents changing the code)

| Part | Path | Notes |
|---|---|---|
| Loader (the DLL Revit loads) | `src/T3LabRevit.Loader/` | `IExternalApplication`, panel **T3Lab MCP** on Revit's own **Add-Ins** tab (never a tab of our own), loads Core from bytes. Keep it small: Revit locks it until restart. |
| Core (server + tools) | `src/T3LabRevit.Core/` | TCP 127.0.0.1:48910-48918, one JSON object per line. Tools in `Tools/*.cs`, each `Registry.Add(...)`. |
| Fair queue | `src/T3LabRevit.Core/Conductor.cs` | One Revit slot for every AI app: lane class, round robin per agent, deadline from the bridge (`queue_wait_s`) → `revit_busy` (`nothing_ran`). Pure .NET, no Revit API: `tests/conductor_test`. |
| Python engine (`send_code_to_revit`) | `src/T3LabRevit.Scripting/` | IronPython 3.4, loaded by reflection from `addin/<year>/scripting` (own AssemblyLoadContext on .NET 8+); stdlib in `%APPDATA%\T3LabRevit\pylib`. Never reference it from Core. |
| Agent Cursors | `src/T3LabRevit.Core/AgentCursors.cs`, `CursorUi.cs` | Records calls on the socket thread; WPF bar + overlay on their own STA thread; never touch the Revit API there. One agent per chat (`client.session`). States: in line, waiting for Revit, working, thinking, idle. |
| Bridge (MCP stdio) | `bridge/t3lab_revit_bridge.py` | Standard library only, Python 3.8+, runs with `-I`. Options `--tier` / `--deny` / `--agent` / `--lane` / `--queue-wait` / `--audit` (docstring). |
| Installer | `install.ps1`, `Install.cmd`, `scripts/register_mcp.py` | PowerShell 5.1 compatible, ASCII only, no admin. |

Rules:
- Revit 2022–2027 from one source: `dotnet build src/<Project>/<Project>.csproj -c Release -p:RevitYear=<year>`
  (2022–2024 net48, 2025–2026 net8, 2027 net10; Revit API from the Nice3point NuGet reference packages). Element ids go
  through `Id.V` / `Id.Make` (`Rv.cs`), never `IntegerValue` / `Value` directly.
- All tool arguments and results are mm, m², m³, degrees. A schema string must not contain `'` or `"` inside a
  description (`Json.Schema` turns `'` into `"`): `python scripts/gen_tools_snapshot.py --check` catches it.
- After adding or changing a tool: `python scripts/gen_tools_snapshot.py` (the bridge lists the snapshot while Revit is closed).
- Gates (CI runs them all): build every year (Core, Loader, Scripting) with `-warnaserror`, `python -I scripts/gen_tools_snapshot.py --check`,
  `python -I tests/smoke_bridge.py`, `python -I tests/test_register.py`, and the Windows install job. After changing `Conductor.cs`:
  `dotnet run --project tests/conductor_test` (the queue's ordering, deadline and one-holder rule, no Revit needed).
- Every tool declares where it can run (`scope:` in `Registry.Add`): `ToolScope.Any` (default: any open model, also one opened
  in the background, pinned with `revit_document`) or `ToolScope.Active` (the UI: selection, the active view, the Python
  engine). Use `c.ActiveView` (null outside the active model) and `c.RequireUi(...)` / `c.SelectedIds()` for a branch that needs
  the UI, never `c.UiDoc` in an Any tool: `python -I scripts/check_tool_scopes.py` (CI) proves it.
- The bridge is also a command line (`bridge.py help`: status, tools, help, call, cursors, reload, connect, verify); every ribbon
  action has one. A new action gets a CLI command and, unless it is the user's (Agent Cursors off / Resume), an MCP tool.
- Error results an agent acts on are JSON `{error_code, message, retryable, nothing_ran, hint}` (add-in: `revit_busy`,
  `paused_by_user`, `needs_active_document`; bridge: `tier_denied`, `tool_denied`, `revit_not_running`, `timeout`, `bridge_error`). Keep the codes stable.
- Shared with [T3Lab CAD MCP](https://github.com/thanhtranarch/t3lab_mcp-cad), the same file but for the namespace:
  `Core/Conductor.cs`, `Core/T3Style.cs` (bar palettes, action / agent colours, every word on the Agent Cursors bar) and
  `Loader/T3Icons.cs` (ribbon icons, T3 Space Line of t3lab-revit-api). The ribbon (buttons, order, tooltips), the bar
  layout and the Status lines match too. Change both repos together.
- Runs side by side with T3Lab (pyRevit, t3lab-revit-api) in the same Revit: keep our server name `t3lab-revit-mcp`, ports
  48910-48918, `%APPDATA%\T3LabRevit`, env vars `T3LABREVIT_*` (theirs: `t3lab-revit`, 48884-48894, `T3LabAI`, `T3LAB_*`).
  Connect AI never touches another server's entry. An `AssemblyResolve` handler answers only for our own DLLs
  (`CodeTools.ResolveOwn`): the AppDomain is shared with pyRevit's IronPython 2.7 / pythonnet and Dynamo. README "Chạy cùng T3Lab".
- Nothing here is verified inside Revit by CI. Say "NEEDS VERIFICATION in Revit" for behaviour you could not run.
