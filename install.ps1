<#
.SYNOPSIS
  T3Lab Revit MCP: install the Revit add-in (2022-2027), the MCP bridge, and connect your AI apps
  (Claude Desktop, Claude Code, Codex/ChatGPT, Cursor, VS Code, Copilot CLI, Gemini CLI, Antigravity, Windsurf).

.DESCRIPTION
  One line, no admin rights, nothing else to install first:
      irm https://raw.githubusercontent.com/thanhtranarch/t3lab_revitmcp/main/install.ps1 | iex
  With options (e.g. no questions, for an AI agent running it for you):
      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/thanhtranarch/t3lab_revitmcp/main/install.ps1))) -Yes
  From a downloaded release (T3LabRevitMCP.zip, unzipped): double-click Install.cmd, or
      powershell -ExecutionPolicy Bypass -File install.ps1

  What it does:
    1. Gets the add-in: from this folder when it is an unzipped release, else -Source, else the latest GitHub release.
    2. Copies it to %APPDATA%\T3LabRevitMCP\addin\<year> and writes %APPDATA%\Autodesk\Revit\Addins\<year>\T3LabRevit.addin
       for each Revit found (2022-2027).
    3. Copies the bridge (bridge.py) and finds Python 3.8+; with none, downloads the embeddable Python (about 11 MB)
       into %APPDATA%\T3LabRevitMCP\python (no admin, nothing on PATH).
    4. Adds the server "t3lab-revit-mcp" to every AI app found (config files backed up first).
  Re-run any time: it updates in place. Restart Revit and the AI app afterwards.

.EXAMPLE
  install.ps1                         # install / update + connect all AI apps found
  install.ps1 -Check                  # report only: what is installed, connected, running (changes nothing)
  install.ps1 -ConnectOnly            # only (re)connect the AI apps
  install.ps1 -Clients claude-desktop,claude-code
  install.ps1 -Years 2024,2026        # only these Revit versions
  install.ps1 -Source C:\Downloads\T3LabRevitMCP.zip
  install.ps1 -Uninstall              # remove the add-in, the bridge and the AI app entries
#>
param(
    [switch]$Check,
    [switch]$ConnectOnly,
    [switch]$Uninstall,
    [string]$Clients = "all",
    [string[]]$Years = @(),     # 2024,2026 / "2024 2026" (via -File the list arrives as one string)
    [string]$Source = "",
    [string]$Python = "",
    [switch]$Yes,
    [switch]$NoClients,
    [switch]$WaitForClaude      # internal: a hidden helper that registers Claude Desktop right after it quits
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$env:PYTHONPATH = ""      # pyRevit sets a machine-wide PYTHONPATH that breaks other interpreters

$Repo = "thanhtranarch/t3lab_revitmcp"
$ReleaseZip = "https://github.com/$Repo/releases/latest/download/T3LabRevitMCP.zip"
$PyEmbed = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip"
$AllYears = 2022..2027
$Data = Join-Path $env:APPDATA "T3LabRevitMCP"
$AddinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$ServerName = "t3lab-revit-mcp"

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Ok($t) { Write-Host "  OK   $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  WARN $t" -ForegroundColor Yellow }
function Info($t) { Write-Host "       $t" }
function Ask($question) {
    if ($Yes) { return $true }
    # no person to answer (an AI agent's shell, CI): never assume yes
    try { if ([Console]::IsInputRedirected) { return $false } } catch { return $false }
    try { $a = Read-Host "$question [Y/n]" } catch { return $false }
    return ($a -eq "" -or $a -match '^(y|yes|c|co)$')
}

if ($env:OS -ne "Windows_NT") { throw "T3Lab Revit MCP installs on Windows (where Revit runs)." }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# ---------------------------------------------------------------- Revit versions
function Find-RevitYears {
    $found = @()
    foreach ($y in $AllYears) {
        $exe = Join-Path $env:ProgramFiles "Autodesk\Revit $y\Revit.exe"
        $reg = "HKLM:\SOFTWARE\Autodesk\Revit\$y"
        if ((Test-Path $exe) -or (Test-Path $reg) -or (Test-Path (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$y"))) { $found += $y }
    }
    return $found
}

# ---------------------------------------------------------------- Python
function Test-Python($exe) {
    try {
        $out = & $exe -I -c "import sys; print('%d.%d|%s' % (sys.version_info[0], sys.version_info[1], sys.executable))" 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $out) { return $null }
        $parts = "$out".Trim().Split("|")
        $v = [version]$parts[0]
        if ($v -ge [version]"3.8") { return $parts[1] }
    } catch { }
    return $null
}

function Find-Python {
    if ($Python) {
        $p = Test-Python $Python
        if ($p) { return $p }
        throw "-Python $Python is not a working Python 3.8+."
    }
    $embedded = Join-Path $Data "python\python.exe"
    if (Test-Path $embedded) { $p = Test-Python $embedded; if ($p) { return $p } }
    # py launcher first; a Microsoft Store alias with no Python behind it just fails Test-Python
    try {
        $out = & py -3 -I -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) { $p = Test-Python "$out".Trim(); if ($p) { return $p } }
    } catch { }
    foreach ($c in @("python", "python3")) {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { $p = Test-Python $cmd.Source; if ($p) { return $p } }
    }
    foreach ($dir in @("$env:LOCALAPPDATA\Programs\Python", "$env:ProgramFiles\Python*", "C:\Python*")) {
        foreach ($exe in (Get-ChildItem -Path $dir -Filter python.exe -Recurse -Depth 2 -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)) {
            $p = Test-Python $exe.FullName
            if ($p) { return $p }
        }
    }
    return $null
}

function Install-EmbeddedPython {
    $dir = Join-Path $Data "python"
    $zip = Join-Path $env:TEMP "t3lab-python-embed.zip"
    Info "No Python 3.8+ found: downloading the embeddable Python 3.12 (python.org, ~11 MB) into $dir"
    Invoke-WebRequest -UseBasicParsing -Uri $PyEmbed -OutFile $zip
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $dir -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Get-ChildItem $dir -Recurse | Unblock-File -ErrorAction SilentlyContinue
    $p = Test-Python (Join-Path $dir "python.exe")
    if (-not $p) { throw "The embeddable Python in $dir does not run. Install Python 3 from python.org and run this again." }
    return $p
}

# ---------------------------------------------------------------- package
function Get-Package {
    if ($Source) {
        if ((Test-Path $Source -PathType Leaf) -and $Source -like "*.zip") { return Expand-Package $Source }
        if (Test-Path (Join-Path $Source "addin")) { return (Resolve-Path $Source).Path }
        if (Test-Path (Join-Path $Source "T3LabRevitMCP\addin")) { return (Resolve-Path (Join-Path $Source "T3LabRevitMCP")).Path }
        throw "-Source $Source is neither T3LabRevitMCP.zip nor an unzipped release (no addin folder)."
    }
    if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot "addin"))) { return $PSScriptRoot }
    $zip = Join-Path $env:TEMP "T3LabRevitMCP.zip"
    Info "Downloading the latest release: $ReleaseZip"
    try { Invoke-WebRequest -UseBasicParsing -Uri $ReleaseZip -OutFile $zip }
    catch { throw "Could not download $ReleaseZip ($($_.Exception.Message)). Download it in a browser and run: install.ps1 -Source <the zip>" }
    return Expand-Package $zip
}

function Expand-Package($zip) {
    $dest = Join-Path $env:TEMP ("T3LabRevitMCP-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    Expand-Archive -Path $zip -DestinationPath $dest -Force
    $root = if (Test-Path (Join-Path $dest "T3LabRevitMCP\addin")) { Join-Path $dest "T3LabRevitMCP" } else { $dest }
    if (-not (Test-Path (Join-Path $root "addin"))) { throw "$zip is not a T3Lab Revit MCP release (no addin folder)." }
    Get-ChildItem $root -Recurse | Unblock-File -ErrorAction SilentlyContinue
    return $root
}

function Write-AddinManifest($year, $dll) {
    $dir = Join-Path $AddinRoot $year
    New-Item -ItemType Directory -Force $dir | Out-Null
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Written by T3Lab Revit MCP install.ps1. Remove with: install.ps1 -Uninstall -->
<RevitAddIns>
  <AddIn Type="Application">
    <Name>T3Lab Revit MCP</Name>
    <Assembly>$dll</Assembly>
    <FullClassName>T3LabRevit.Loader.LoaderApp</FullClassName>
    <ClientId>5C1D3E9A-7B42-4F6E-9A3D-2E8B7C41F0A3</ClientId>
    <VendorId>T3LAB</VendorId>
    <VendorDescription>T3Lab, https://github.com/$Repo</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    [IO.File]::WriteAllText((Join-Path $dir "T3LabRevit.addin"), $xml, (New-Object Text.UTF8Encoding $false))
}

function Invoke-Register([string[]]$extra) {
    $py = Find-Python
    if (-not $py) { Warn "No Python: run install.ps1 (without -ConnectOnly) once to set it up."; return 1 }
    $reg = Join-Path $Data "register_mcp.py"
    $bridge = Join-Path $Data "bridge.py"
    & $py -I $reg --bridge $bridge --python $py --clients $Clients @extra | ForEach-Object {
        if ($_ -notlike "{*") { Write-Host "  $_" }
    }
    return $LASTEXITCODE
}

function Get-ClaudeDesktop {
    Get-Process claude -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path -like "*\AnthropicClaude\*" -or $_.Path -like "*\WindowsApps\*")
    }
}

function Connect-Clients {
    Step "Connect AI apps ($Clients)"
    $restart = $null
    if ($Clients -eq "all" -or $Clients -like "*claude-desktop*") {
        $running = @(Get-ClaudeDesktop)
        if ($running.Count -gt 0) {
            Info "Claude Desktop is running; it rewrites its config when it quits, so it must close for the change to stay."
            if (Ask "  Close Claude Desktop now and reopen it after?") {
                $restart = ($running | Select-Object -First 1).Path
                $running | ForEach-Object { try { $_.CloseMainWindow() | Out-Null } catch { } }
                Start-Sleep -Seconds 2
                Get-ClaudeDesktop | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
            }
        }
    }
    $code = Invoke-Register @()
    if ($code -eq 3) {
        # still running: a hidden helper adds it the moment Claude Desktop quits (it would overwrite the change otherwise)
        try {
            Start-Process powershell -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                "`"$(Join-Path $Data 'install.ps1')`"", "-WaitForClaude")
            Warn "Claude Desktop is open, so it is added the moment you quit it. Quit Claude Desktop (tray icon > Quit) and open it again."
        } catch {
            Warn "Claude Desktop was skipped (still running). Quit it from the tray icon, then run: install.ps1 -ConnectOnly"
        }
    }
    if ($restart) {
        try { Start-Process $restart; Ok "Claude Desktop restarted" } catch { Warn "Start Claude Desktop again yourself." }
    }
    Info "Restart any other AI app that was open (Claude Code, Cursor, VS Code...) so it loads the new server."
}

# ---------------------------------------------------------------- modes
if ($WaitForClaude) {
    $log = Join-Path $Data "logs\connect-claude.log"
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    $deadline = (Get-Date).AddHours(4)
    while (@(Get-ClaudeDesktop).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
    Start-Sleep -Seconds 2
    $Clients = "claude-desktop"
    $py = Find-Python
    if ($py) {
        & $py -I (Join-Path $Data "register_mcp.py") --bridge (Join-Path $Data "bridge.py") --python $py --clients claude-desktop *>> $log
    }
    return
}

if ($Check) {
    Step "T3Lab Revit MCP - check (changes nothing)"
    $ver = if (Test-Path (Join-Path $Data "version.txt")) { (Get-Content (Join-Path $Data "version.txt") -Raw).Trim() } else { "not installed" }
    Info "Installed version : $ver  ($Data)"
    $revit = Find-RevitYears
    Info "Revit found       : $(if ($revit) { $revit -join ', ' } else { 'none in Program Files' })"
    foreach ($y in $AllYears) {
        $m = Join-Path $AddinRoot "$y\T3LabRevit.addin"
        if (Test-Path $m) {
            $dll = ([xml](Get-Content $m -Raw)).RevitAddIns.AddIn.Assembly
            if (Test-Path $dll) { Ok "Revit $y add-in -> $dll" } else { Warn "Revit $y manifest points to a missing DLL: $dll" }
        }
    }
    $py = Find-Python
    if ($py) { Ok "Python: $py" } else { Warn "No Python 3.8+ (install.ps1 sets one up)" }
    if ($py -and (Test-Path (Join-Path $Data "bridge.py"))) {
        Step "AI apps"
        & $py -I (Join-Path $Data "register_mcp.py") --check --clients $Clients | ForEach-Object { if ($_ -notlike "{*") { Write-Host "  $_" } }
        Step "Revit connection"
        $out = & $py -I (Join-Path $Data "bridge.py") --check
        $state = $out | ConvertFrom-Json
        if ($state.revit_running) {
            foreach ($i in $state.instances) { Ok "Revit $($i.revit) on port $($i.port), model: $($i.active_document)" }
        } else { Warn "No Revit with the add-in is running right now (open Revit, then check again)." }
        Write-Output $out
    }
    return
}

if ($Uninstall) {
    Step "Uninstall T3Lab Revit MCP"
    if (-not (Ask "Remove the add-in, the bridge and the '$ServerName' entries from your AI apps?")) { return }
    if (Test-Path (Join-Path $Data "register_mcp.py")) { Invoke-Register @("--remove") | Out-Null }
    foreach ($y in $AllYears) {
        $m = Join-Path $AddinRoot "$y\T3LabRevit.addin"
        if (Test-Path $m) { Remove-Item $m -Force; Ok "Removed $m" }
    }
    try { Remove-Item $Data -Recurse -Force; Ok "Removed $Data" }
    catch { Warn "Some files in $Data are in use (Revit is open): close Revit and delete the folder." }
    return
}

if ($ConnectOnly) {
    if (-not (Test-Path (Join-Path $Data "bridge.py"))) { throw "Not installed yet: run install.ps1 without -ConnectOnly." }
    Connect-Clients
    return
}

# ---------------------------------------------------------------- install
Write-Host "T3Lab Revit MCP installer" -ForegroundColor Cyan
Step "Package"
$pkg = Get-Package
$manifest = Get-Content (Join-Path $pkg "manifest.json") -Raw | ConvertFrom-Json
Ok "T3Lab Revit MCP $($manifest.version) ($pkg)"

Step "Revit add-in"
$wanted = @($Years | ForEach-Object { "$_" -split '[,; ]+' } | Where-Object { $_ -match '^\d{4}$' } | ForEach-Object { [int]$_ })
$targets = if ($wanted.Count -gt 0) { $wanted } else { Find-RevitYears }
if (-not $targets -or $targets.Count -eq 0) {
    Warn "No Revit 2022-2027 found in Program Files: installing for all of them (harmless)."
    $targets = $AllYears
}
$restartRevit = $false
$revitRunning = @(Get-Process Revit -ErrorAction SilentlyContinue).Count -gt 0
foreach ($y in $targets) {
    $src = Join-Path $pkg "addin\$y"
    if (-not (Test-Path $src)) { Warn "Revit ${y}: not in this package, skipped"; continue }
    $dst = Join-Path $Data "addin\$y"
    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($d in Get-ChildItem $src -Directory) {
        # scripting\ (IronPython): locked by Revit once send_code_to_revit has run
        try { Copy-Item $d.FullName $dst -Recurse -Force }
        catch {
            Warn "Revit ${y}: $($d.Name) is in use; close Revit and run this again to update it."
            $restartRevit = $true
        }
    }
    foreach ($f in Get-ChildItem $src -File) {
        $to = Join-Path $dst $f.Name
        if ((Test-Path $to) -and ((Get-FileHash $to).Hash -eq (Get-FileHash $f.FullName).Hash)) { continue }
        try { Copy-Item $f.FullName $to -Force }
        catch {
            # only the loader is locked by a running Revit (the core is loaded from bytes)
            Warn "Revit ${y}: $($f.Name) is in use; close Revit and run this again to update it."
            $restartRevit = $true
        }
    }
    Write-AddinManifest $y (Join-Path $dst "T3LabRevit.Loader.dll")
    Ok "Revit $y"
}

Step "Bridge"
New-Item -ItemType Directory -Force $Data | Out-Null
Copy-Item (Join-Path $pkg "bridge\bridge.py") (Join-Path $Data "bridge.py") -Force
Copy-Item (Join-Path $pkg "bridge\tools_snapshot.json") (Join-Path $Data "tools_snapshot.json") -Force
Copy-Item (Join-Path $pkg "scripts\register_mcp.py") (Join-Path $Data "register_mcp.py") -Force
foreach ($doc in "install.ps1", "Install.cmd", "README.md", "INSTALL_FOR_AI.md") {
    $p = Join-Path $pkg $doc
    if (Test-Path $p) { Copy-Item $p (Join-Path $Data $doc) -Force }
}
$pylib = Join-Path $pkg "pylib"
if (Test-Path $pylib) {
    # Python standard library for send_code_to_revit (shared by every Revit year)
    $dstLib = Join-Path $Data "pylib"
    if (Test-Path $dstLib) { Remove-Item $dstLib -Recurse -Force -ErrorAction SilentlyContinue }
    Copy-Item $pylib $dstLib -Recurse -Force
    Ok "$dstLib (Python standard library for send_code_to_revit)"
}
Set-Content -Path (Join-Path $Data "version.txt") -Value $manifest.version -Encoding ASCII
Ok "$Data\bridge.py"

Step "Python"
$py = Find-Python
if (-not $py) { $py = Install-EmbeddedPython }
Ok $py

if (-not $NoClients) { Connect-Clients }

Step "Done"
if ($restartRevit -or $revitRunning) {
    Write-Host "  Restart Revit to load the add-in (tab 'T3Lab MCP')." -ForegroundColor Yellow
} else {
    Write-Host "  Open Revit: the tab 'T3Lab MCP' appears and the MCP server starts by itself." -ForegroundColor Green
}
Info "Revit 2026+ may ask once whether to load an unsigned add-in: choose 'Always Load'."
Info "Then ask your AI app, e.g.: 'Use t3lab-revit-mcp: what model is open in Revit and how many walls does it have?'"
Info "Check everything later with: & `"$Data\install.ps1`" -Check"
