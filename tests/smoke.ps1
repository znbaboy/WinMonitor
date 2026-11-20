# Launch tests + screenshots on a Windows machine (GitHub Actions). Usage: tests/smoke.ps1 -Dir publish/WinMonitor
param([Parameter(Mandatory)] [string]$Dir, [string]$Shots = "screenshots")
$ErrorActionPreference = 'Stop'
$Dir = (Resolve-Path $Dir).Path
$exe = Join-Path $Dir 'WinMonitor.exe'
$Data = Join-Path $Dir '.winmonitor'   # history, logs and backups live here; settings.json is next to the exe
New-Item -ItemType Directory -Force $Shots | Out-Null
$Shots = (Resolve-Path $Shots).Path
$env:WINMONITOR_NO_DIALOG = '1'   # no message boxes (driver / download prompts answer No)

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  public struct RECT { public int L, T, R, B; }
  public struct POINT { public int X, Y; }
  public static List<IntPtr> Find(uint pid, string title, bool exact) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256); var t = sb.ToString();
        if (exact ? t == title : t.Contains(title)) list.Add(h);
      }
      return true; }, IntPtr.Zero);
    return list;
  }
}
"@
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@

function Drag-MouseY([int]$x, [int]$y1, [int]$y2) {
  $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
  function Abs([int]$px, [int]$py) { [M]::mouse_event(0x8001, [uint32]($px * 65535 / $sw), [uint32]($py * 65535 / $sh), 0, [UIntPtr]::Zero) }
  Abs $x $y1; Start-Sleep -Milliseconds 300
  [M]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150
  $step = if ($y2 -ge $y1) { 3 } else { -3 }
  for ($yy = $y1; [Math]::Abs($yy - $y2) -gt 2; $yy += $step) { Abs $x $yy; Start-Sleep -Milliseconds 20 }
  Abs $x $y2; Start-Sleep -Milliseconds 300
  [M]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep 2
}

function Drag-Mouse([int]$x, [int]$y1, [int]$y2) {
  # real mouse input (absolute moves), so Windows drag and drop sees it
  $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width; $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
  function Abs([int]$px, [int]$py) { [M]::mouse_event(0x8001, [uint32]($px * 65535 / $sw), [uint32]($py * 65535 / $sh), 0, [UIntPtr]::Zero) }
  Abs $x $y1; Start-Sleep -Milliseconds 300
  [M]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150
  for ($yy = $y1; $yy -le $y2; $yy += 3) { Abs $x $yy; Start-Sleep -Milliseconds 20 }
  Start-Sleep -Milliseconds 300
  [M]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep 2
}

$script:failed = @()
function Fail([string]$m) { Write-Host "FAIL: $m" -ForegroundColor Red; $script:failed += $m }
function Ok([string]$m) { Write-Host "OK: $m" -ForegroundColor Green }

function Stop-App {
  Get-Process WinMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep 2
}

function Start-App([string]$settingsJson = $null, [string[]]$appArgs = @()) {
  Stop-App
  New-Item -ItemType Directory -Force $Data | Out-Null
  Remove-Item "$Dir\settings.json*", "$Data\settings.json*" -ErrorAction SilentlyContinue
  if ($settingsJson) { Set-Content "$Dir\settings.json" $settingsJson -Encoding UTF8 }
  if ($appArgs.Count -gt 0) { $p = Start-Process $exe -ArgumentList $appArgs -PassThru } else { $p = Start-Process $exe -PassThru }
  Start-Sleep 20
  if ($p.HasExited) { Fail "app exited early (code $($p.ExitCode))"; return $null }
  return $p
}

function Panel($p) { [W]::Find([uint32]$p.Id, 'WinMonitor', $true) | Select-Object -First 1 }

function Shot([IntPtr]$h, [string]$name, [int]$margin = 0, [bool]$dwm = $false) {
  $r = New-Object W+RECT
  if ($dwm) { [W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null } else { [W]::GetWindowRect($h, [ref]$r) | Out-Null }
  $x = [Math]::Max(0, $r.L - $margin); $y = [Math]::Max(0, $r.T - $margin)
  $w = ($r.R - $r.L) + 2 * $margin; $hh = ($r.B - $r.T) + 2 * $margin
  if ($w -le 0 -or $hh -le 0) { Fail "$name has no size"; return }
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $hh))
  $bmp.Save("$Shots\$name.png")
  $g.Dispose(); $bmp.Dispose()
  Write-Host "screenshot $name ($($r.R - $r.L)x$($r.B - $r.T))"
}

function Window($p, [string]$title, [string]$name, [int]$w = 900, [int]$h = 700) {
  $win = [W]::Find([uint32]$p.Id, $title, $false) | Select-Object -First 1
  if (-not $win) { Fail "window '$title' not found"; return }
  [W]::SetWindowPos($win, [IntPtr]::Zero, 60, 30, $w, $h, 0x40) | Out-Null
  [W]::SetForegroundWindow($win) | Out-Null
  Start-Sleep 3
  Shot $win $name 0 $true
  Ok "window '$title'"
}

# dark app theme for the windows
reg add HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize /v AppsUseLightTheme /t REG_DWORD /d 0 /f | Out-Null

# ---------------------------------------------------------------- 1. default launch
$p = Start-App '{ "UIScale": 0.9 }'
if ($p) {
  $h = Panel $p
  if (-not $h) { Fail 'panel window not visible' }
  else {
    $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $ex = [W]::GetWindowLong($h, -20)
    $st = [W]::GetWindowLong($h, -16)
    Write-Host ('STYLES: style 0x{0:X8} exstyle 0x{1:X8}' -f $st, $ex)
    $child = [W]::FindWindowEx($h, [IntPtr]::Zero, $null, $null); $cr = New-Object W+RECT; [W]::GetWindowRect($child, [ref]$cr) | Out-Null
    Write-Host ('RECTS: window {0},{1},{2},{3}  first child {4},{5},{6},{7}' -f $r.L, $r.T, $r.R, $r.B, $cr.L, $cr.T, $cr.R, $cr.B)
    if (($r.R - $r.L) -lt 100 -or ($r.B - $r.T) -lt 100) { Fail "panel too small: $($r.R - $r.L)x$($r.B - $r.T)" } else { Ok "panel $($r.R - $r.L)x$($r.B - $r.T)" }
    if (Test-Path "$Dir\settings.json") { Ok 'settings.json created on first start' } else { Fail 'settings.json not created on first start' }
    if (($ex -band 0x80) -eq 0) { Fail 'panel is not a tool window (would show in the taskbar)' } else { Ok 'tool window' }
    if (($ex -band 0x8) -ne 0) { Fail 'panel is always on top, but that is off by default' } else { Ok 'not on top by default' }
    Shot $h 'panel-default' 24
  }

  # second launch must exit and leave one instance
  $p2 = Start-Process $exe -PassThru
  Start-Sleep 6
  if (-not $p2.HasExited) { Stop-Process $p2 -Force; Fail 'second instance did not exit' } else { Ok 'single instance' }
  if ($p.HasExited) { Fail 'first instance died after the second launch' }

  # memory after idling (the app trims itself; Task Manager's "Memory" column is the private working set)
  Start-Sleep 50
  $pws = (Get-Counter '\Process(WinMonitor)\Working Set - Private').CounterSamples[0].CookedValue / 1MB
  $proc = Get-Process WinMonitor | Select-Object -First 1
  "MEMORY: private working set {0:N1} MB, working set {1:N1} MB, private bytes {2:N1} MB" -f $pws, ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB) | Write-Host
  if ($pws -gt 45) { Fail ("private working set {0:N1} MB is above 45 MB" -f $pws) } else { Ok ("memory {0:N1} MB" -f $pws) }
}

# ---------------------------------------------------------------- 2. light and dark looks
$p = Start-App '{ "Skin": "Light", "UIScale": 0.85 }'
if ($p) { $h = Panel $p; if ($h) { Shot $h 'panel-light' 24; Ok 'light look' } else { Fail 'light look: no panel' } }
$p = Start-App '{ "Skin": "Dark", "Opacity": 0.6, "PanelWidth": 300, "UIScale": 0.85 }'
if ($p) { $h = Panel $p; if ($h) { Shot $h 'panel-dark-60' 24; Ok 'dark look, 60% opacity' } else { Fail 'dark look: no panel' } }

# ---------------------------------------------------------------- 4. click-through
$p = Start-App '{ "ClickThrough": true, "UIScale": 0.9 }'
if ($p) {
  $h = Panel $p
  if (-not $h) { Fail 'click-through: panel not visible' }
  else {
    Shot $h 'panel-clickthrough' 24
    $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $pt = New-Object W+POINT; $pt.X = [int](($r.L + $r.R) / 2); $pt.Y = [int](($r.T + $r.B) / 2)
    $hit = [W]::GetAncestor([W]::WindowFromPoint($pt), 2)
    if ($hit -eq $h) { Fail 'click-through: the panel still takes clicks' } else { Ok 'click-through passes clicks on' }
  }
}

# ---------------------------------------------------------------- 5. windows
$p = Start-App $null @('--open', 'settings')
if ($p) { Window $p 'Settings - WinMonitor' 'settings-panel' }
$p = Start-App $null @('--open', 'settings:Items')
if ($p) { Window $p 'Settings - WinMonitor' 'settings-items' }
$env:WINMONITOR_EXPAND_ALL = '1'; $env:WINMONITOR_TRACE = '1'
$p = Start-App '{ "DisabledGroups": ["GPU"] }' @('--open', 'settings:Items')
if ($p) { Window $p 'Settings - WinMonitor' 'settings-items-expanded' 900 1000 }
if ($p) {
  # drag the DASH.HOST row below DASH.Time with the real mouse, then check the saved order
  try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $win = [W]::Find([uint32]$p.Id, 'Settings - WinMonitor', $false) | Select-Object -First 1
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win)
    function Find-Handle($n) {
      $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Move $n")
      $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    }
    $a = Find-Handle 'DASH.HOST'; $b = Find-Handle 'DASH.Time'
    if (-not $a -or -not $b) { Fail 'drag test: handles not found' }
    else {
      $ra = $a.Current.BoundingRectangle; $rb = $b.Current.BoundingRectangle
      Drag-Mouse ([int]($ra.X + $ra.Width / 2)) ([int]($ra.Y + $ra.Height / 2)) ([int]($rb.Y + $rb.Height + 8))
      Start-Sleep -Milliseconds 900   # settings are written 0.4 s after the last change
      $json = Get-Content "$Dir\settings.json" -Raw | ConvertFrom-Json
      $host1 = ($json.MonitorItems | Where-Object Key -eq 'DASH.HOST').SortIndex
      $time1 = ($json.MonitorItems | Where-Object Key -eq 'DASH.Time').SortIndex
      Write-Host "DRAG: DASH.HOST=$host1 DASH.Time=$time1"
      if (Test-Path "$Data\WinMonitor_Trace.log") { Get-Content "$Data\WinMonitor_Trace.log" | Select-Object -First 15 | ForEach-Object { Write-Host "TRACE $_" } } else { Write-Host 'TRACE: no trace written (press never reached the handle)' }
      if ($host1 -gt $time1) { Ok 'drag to reorder rows' } else { Fail 'drag to reorder rows did not change the order' }
      Shot $win 'settings-items-dragged' 0 $true
      # drag the expanded Devices category up above Dashboard
      $ua = Find-Handle 'HALO'; $ub = Find-Handle 'DASH'
      if ($ua -and $ub) {
        $r1 = $ua.Current.BoundingRectangle; $r2 = $ub.Current.BoundingRectangle
        Drag-MouseY ([int]($r1.X + $r1.Width / 2)) ([int]($r1.Y + $r1.Height / 2)) ([int]($r2.Y - 4))
        Start-Sleep -Milliseconds 900   # settings are written 0.4 s after the last change
        $json = Get-Content "$Dir\settings.json" -Raw | ConvertFrom-Json
        $d = ($json.MonitorItems | Where-Object Key -eq 'DASH.Uptime').SortIndex; $h = ($json.MonitorItems | Where-Object Key -eq 'HALO.Mouse').SortIndex
        Write-Host "DRAG CATEGORY UP: DASH.Uptime=$d HALO.Mouse=$h"
        Shot $win 'settings-items-category-up' 0 $true
        if ($h -lt $d) { Ok 'drag an expanded category up' } else { Fail 'drag an expanded category up did not change the order' }
      } else { Fail 'category handles not found (up)' }

      # collapse all, then drag Devices back below Dashboard
      $cc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Collapse all')
      $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cc)
      if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2 }
      $ca = Find-Handle 'HALO'; $cb = Find-Handle 'DASH'
      if ($ca -and $cb) {
        $r1 = $ca.Current.BoundingRectangle; $r2 = $cb.Current.BoundingRectangle
        Drag-Mouse ([int]($r1.X + $r1.Width / 2)) ([int]($r1.Y + $r1.Height / 2)) ([int]($r2.Y + $r2.Height + 12))
        Start-Sleep -Milliseconds 900   # settings are written 0.4 s after the last change
        $json = Get-Content "$Dir\settings.json" -Raw | ConvertFrom-Json
        $d = ($json.MonitorItems | Where-Object Key -eq 'DASH.Uptime').SortIndex; $h = ($json.MonitorItems | Where-Object Key -eq 'HALO.Mouse').SortIndex
        Write-Host "DRAG CATEGORY DOWN: DASH.Uptime=$d HALO.Mouse=$h"
        Shot $win 'settings-items-category-dragged' 0 $true
        if ($d -lt $h) { Ok 'drag a collapsed category down' } else { Fail 'drag a collapsed category down did not change the order' }
      } else { Fail 'category handles not found' }
    }
  } catch { Fail "drag test: $_" }
}
$env:WINMONITOR_EXPAND_ALL = $null; $env:WINMONITOR_TRACE = $null; Remove-Item "$Data\WinMonitor_Trace.log" -ErrorAction SilentlyContinue
$p = Start-App $null @('--open', 'settings:Hardware')
if ($p) { Start-Sleep 5; Window $p 'Settings - WinMonitor' 'settings-hardware' }
$p = Start-App $null @('--open', 'settings:Alerts')
if ($p) { Window $p 'Settings - WinMonitor' 'settings-alerts' }
$p = Start-App $null @('--open', 'settings:About')
if ($p) { Window $p 'Settings - WinMonitor' 'settings-about' }
$p = Start-App $null @('--open', 'history')
if ($p) { Window $p 'Monitor History' 'history' }
$p = Start-App $null @('--open', 'hardware')
if ($p) { Window $p 'Hardware Info' 'hardware-info' }
$p = Start-App $null @('--open', 'themes')
if ($p) { Window $p 'Theme editor' 'theme-editor' }

# Windows 11 dialogs: the reset question over Settings (ContentDialog), and a stand-alone dialog window
$env:WINMONITOR_NO_DIALOG = $null
$p = Start-App $null @('--open', 'settings:About')
if ($p) {
  try {
    # with dialogs on, the PawnIO question (no driver on the test machine) comes first: answer Not now
    $pw = $null
    for ($i = 0; $i -lt 20 -and -not $pw; $i++) { $pw = [W]::Find([uint32]$p.Id, 'Install the PawnIO driver?', $false) | Select-Object -First 1; if (-not $pw) { Start-Sleep -Milliseconds 500 } }
    if ($pw) {
      $pr = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$pw)
      $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Not now')
      $nb = $pr.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
      if ($nb) { $nb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    }
    $win = $null
    for ($i = 0; $i -lt 30 -and -not $win; $i++) { $win = [W]::Find([uint32]$p.Id, 'Settings - WinMonitor', $false) | Select-Object -First 1; if (-not $win) { Start-Sleep -Milliseconds 500 } }
    if (-not $win) { throw 'settings window not found' }
    $win = [IntPtr]$win
    [W]::SetWindowPos($win, [IntPtr]::Zero, 60, 30, 900, 700, 0x40) | Out-Null
    Start-Sleep 2
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win)
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Reset…')
    $btn = $null
    for ($i = 0; $i -lt 20 -and -not $btn; $i++) { $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c); if (-not $btn) { Start-Sleep -Milliseconds 500 } }
    if (-not $btn) { Shot $win 'dialog-reset-missing' 0 $true; Write-Host 'WARN: reset button not found (UI Automation); dialog not tested' }
    else {
      $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep 3
      Shot $win 'dialog-reset' 0 $true
      $cc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Cancel')
      $cancel = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cc)
      if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Ok 'reset dialog' } else { Fail 'reset dialog: no Cancel button' }
    }
  } catch { Fail "reset dialog: $_" }
}
$env:WINMONITOR_DIALOG_DEMO = '1'
$p = Start-App
if ($p) {
  $win = [W]::Find([uint32]$p.Id, 'Install the PawnIO driver?', $false) | Select-Object -First 1
  if ($win) { [W]::SetForegroundWindow($win) | Out-Null; Start-Sleep 2; Shot $win 'dialog-window' 0 $true; Ok 'dialog window' } else { Fail 'dialog window not found' }
}
$env:WINMONITOR_DIALOG_DEMO = $null; $env:WINMONITOR_NO_DIALOG = '1'
Stop-App

# ---------------------------------------------------------------- 5b. Start with Windows
# turn it on in settings.json, start the app: it must register the scheduled task for this exe,
# and running that task (what Windows does at sign-in) must start WinMonitor
cmd /c "schtasks /Delete /TN WinMonitor_AutoStart /F >nul 2>&1"
$j = Get-Content "$Dir\settings.json" -Raw | ConvertFrom-Json
$j.AutoStart = $true
$j | ConvertTo-Json -Depth 20 | Set-Content "$Dir\settings.json" -Encoding UTF8
$p = Start-Process $exe -PassThru
Start-Sleep 20
$xml = (cmd /c "schtasks /Query /TN WinMonitor_AutoStart /XML 2>nul") -join "`n"
if (-not $xml) { Fail 'Start with Windows: no scheduled task' }
else {
  $tx = [xml]$xml
  $cmd = $tx.Task.Actions.Exec.Command; $lvl = $tx.Task.Principals.Principal.RunLevel
  if ($cmd -eq $exe) { Ok "task runs $cmd" } else { Fail "task command is '$cmd', expected '$exe'" }
  if ($lvl -eq 'HighestAvailable') { Ok 'task runs as administrator' } else { Fail "task run level '$lvl'" }
  if ($tx.Task.Triggers.LogonTrigger) { Ok 'task starts at sign-in' } else { Fail 'task has no sign-in trigger' }
  Stop-App
  cmd /c "schtasks /Run /TN WinMonitor_AutoStart >nul 2>&1"
  Start-Sleep 15
  if (Get-Process WinMonitor -ErrorAction SilentlyContinue) { Ok 'running the task starts WinMonitor' } else { Fail 'running the task did not start WinMonitor' }
}
Stop-App
cmd /c "schtasks /Delete /TN WinMonitor_AutoStart /F >nul 2>&1"

# ---------------------------------------------------------------- 5c. custom names with emojis survive a restart
$j = Get-Content "$Dir\settings.json" -Raw | ConvertFrom-Json
($j.MonitorItems | Where-Object Key -eq 'HALO.Mouse').UserLabel = "$([char]::ConvertFromUtf32(0x1F5B1)) My mouse"
$j.AutoStart = $false
$j.LastUpdateCheckUtc = '2000-01-01T00:00:00Z'   # the update check saves settings.json a few seconds after start
$j | ConvertTo-Json -Depth 20 | Set-Content "$Dir\settings.json" -Encoding UTF8
$p = Start-Process $exe -PassThru
Start-Sleep 30
Stop-App
$j = Get-Content "$Dir\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$label = ($j.MonitorItems | Where-Object Key -eq 'HALO.Mouse').UserLabel
if ($label -eq "$([char]::ConvertFromUtf32(0x1F5B1)) My mouse") { Ok 'emoji name kept after restart' } else { Fail "emoji name lost after restart (now '$label')" }

# ---------------------------------------------------------------- 6. errors
$log = Join-Path $Data 'WinMonitor_Error.log'
if (Test-Path $log) { Get-Content $log | Select-Object -First 80; Fail 'WinMonitor_Error.log was written' } else { Ok 'no error log' }
Remove-Item $Data -Recurse -ErrorAction SilentlyContinue

if ($script:failed.Count -gt 0) { Write-Host "`n$($script:failed.Count) check(s) failed:"; $script:failed | ForEach-Object { Write-Host " - $_" }; exit 1 }
Write-Host "`nAll checks passed."
