"""Assemble the release: dist/T3LabRevitMCP.zip

    python scripts/package.py            # after building every Revit year (see README, or CI)

Zip layout (one folder, so "Extract all" + Install.cmd just works):
    T3LabRevitMCP/install.ps1, Install.cmd, README.md, INSTALL_FOR_AI.md, LICENSE
    T3LabRevitMCP/addin/<2022..2027>/T3LabRevit.Loader.dll, T3LabRevit.Core.dll (+ .pdb)
    T3LabRevitMCP/addin/<year>/scripting/   IronPython 3.4 engine for send_code_to_revit
    T3LabRevitMCP/pylib/                    Python standard library for it (IronPython.StdLib, trimmed)
    T3LabRevitMCP/bridge/bridge.py, tools_snapshot.json
    T3LabRevitMCP/scripts/register_mcp.py
    T3LabRevitMCP/manifest.json   {"name", "version", "years": {...}, "server"}

Standard library only.
"""
import json
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
YEARS = {
    "2022": ".NET Framework 4.8", "2023": ".NET Framework 4.8", "2024": ".NET Framework 4.8",
    "2025": ".NET 8", "2026": ".NET 8", "2027": ".NET 10",
}
PROJECTS = ("T3LabRevit.Loader", "T3LabRevit.Core")
TOP = "T3LabRevitMCP"
STDLIB_VERSION = "3.4.2"
# not needed inside Revit, or not usable there (GUI, installers, tests): 13 MB -> about 6 MB
STDLIB_SKIP = {"ensurepip", "distutils", "pydoc_data", "lib2to3", "idlelib", "tkinter", "turtledemo", "test", "tests",
               "unittest", "venv", "turtle.py", "site-packages", "__pycache__"}


def version():
    with open(os.path.join(ROOT, "src", "Directory.Build.props"), encoding="utf-8") as f:
        return re.search(r"<Version>([^<]+)</Version>", f.read()).group(1)


def entries(configuration="Release"):
    """[(source path, path in the zip)]; raises when a year was not built."""
    out, missing = [], []
    for year in YEARS:
        for project in PROJECTS:
            for ext in (".dll", ".pdb"):
                src = os.path.join(ROOT, "src", project, "bin", configuration, year, project + ext)
                if not os.path.isfile(src):
                    if ext == ".dll":
                        missing.append(src)
                    continue
                out.append((src, "{}/addin/{}/{}{}".format(TOP, year, project, ext)))
        scripting = os.path.join(ROOT, "src", "T3LabRevit.Scripting", "bin", configuration, year, "scripting")
        if not os.path.isfile(os.path.join(scripting, "T3LabRevit.Scripting.dll")):
            missing.append(scripting)
        else:
            for name in sorted(os.listdir(scripting)):
                path = os.path.join(scripting, name)
                # native Mono.Unix builds for other OSes and the console runtimeconfig are not used inside Revit
                if os.path.isfile(path) and not name.endswith(".runtimeconfig.json") and not name.endswith(".deps.json"):
                    out.append((path, "{}/addin/{}/scripting/{}".format(TOP, year, name)))
    if missing:
        raise SystemExit("Not built:\n  " + "\n  ".join(missing))
    stdlib = os.path.join(os.environ.get("NUGET_PACKAGES") or os.path.join(os.path.expanduser("~"), ".nuget", "packages"),
                          "ironpython.stdlib", STDLIB_VERSION, "content", "lib")
    if not os.path.isdir(stdlib):
        raise SystemExit("IronPython.StdLib not restored: " + stdlib)
    for dirpath, dirnames, filenames in os.walk(stdlib):
        dirnames[:] = sorted(d for d in dirnames if d not in STDLIB_SKIP)
        for name in sorted(filenames):
            if name in STDLIB_SKIP or name.endswith(".pyc"):
                continue
            path = os.path.join(dirpath, name)
            out.append((path, TOP + "/pylib/" + os.path.relpath(path, stdlib).replace(os.sep, "/")))
    for src, arc in (("bridge/t3lab_revit_bridge.py", "bridge/bridge.py"), ("bridge/tools_snapshot.json", "bridge/tools_snapshot.json"),
                     ("scripts/register_mcp.py", "scripts/register_mcp.py"), ("install.ps1", "install.ps1"),
                     ("Install.cmd", "Install.cmd"), ("README.md", "README.md"), ("INSTALL_FOR_AI.md", "INSTALL_FOR_AI.md"),
                     ("LICENSE", "LICENSE")):
        path = os.path.join(ROOT, src)
        if os.path.isfile(path):
            out.append((path, TOP + "/" + arc))
        elif src != "LICENSE":
            raise SystemExit("missing " + src)
    return out


def build(dest=None):
    dest = dest or os.path.join(ROOT, "dist", "T3LabRevitMCP.zip")
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    manifest = {"name": "T3LabRevitMCP", "version": version(), "years": YEARS, "server": "t3lab-revit-mcp",
                "repo": "https://github.com/thanhtranarch/t3lab_revitmcp"}
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
        for src, arc in entries():
            z.write(src, arc)
        z.writestr(TOP + "/manifest.json", json.dumps(manifest, indent=2))
    return dest


if __name__ == "__main__":
    path = build(sys.argv[1] if len(sys.argv) > 1 else None)
    print("{} ({} bytes)".format(path, os.path.getsize(path)))
