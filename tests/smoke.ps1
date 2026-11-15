# Launch tests + screenshots on a Windows machine (GitHub Actions). Usage: tests/smoke.ps1 -Dir publish/WinMonitor
param([Parameter(Mandatory)] [string]$Dir, [string]$Shots = "screenshots")
$ErrorActionPreference = 'Stop'
$Dir = (Resolve-Path $Dir).Path
$exe = Join-Path $Dir 'WinMonitor.exe'
New-Item -ItemType Directory -Force $Shots | Out-Null
$Shots = (Resolve-Path $Shots).Path
$env:WINMONITOR_NO_DIALOG = '1'   # no message boxes (driver / download prompts answer No)

Add-Type -AssemblyName System.Drawing
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

$script:failed = @()
function Fail([string]$m) { Write-Host "FAIL: $m" -ForegroundColor Red; $script:failed += $m }
function Ok([string]$m) { Write-Host "OK: $m" -ForegroundColor Green }

function Stop-App {
  Get-Process WinMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep 2
}

function Start-App([string]$settingsJson = $null, [string[]]$appArgs = @()) {
  Stop-App
  Remove-Item "$Dir\settings.json*" -ErrorAction SilentlyContinue
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
$p = Start-App '{ "AutoHide": false, "UIScale": 0.9 }'
if ($p) {
  $h = Panel $p
  if (-not $h) { Fail 'panel window not visible' }
  else {
    $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $ex = [W]::GetWindowLong($h, -20)
    if (($r.R - $r.L) -lt 100 -or ($r.B - $r.T) -lt 100) { Fail "panel too small: $($r.R - $r.L)x$($r.B - $r.T)" } else { Ok "panel $($r.R - $r.L)x$($r.B - $r.T)" }
    if (($ex -band 0x80) -eq 0) { Fail 'panel is not a tool window (would show in the taskbar)' } else { Ok 'tool window' }
    if (($ex -band 0x8) -eq 0) { Fail 'panel is not always on top' } else { Ok 'always on top' }
    Shot $h 'panel-default' 24
  }

  # second launch must exit and leave one instance
  $p2 = Start-Process $exe -PassThru
  Start-Sleep 6
  if (-not $p2.HasExited) { Stop-Process $p2 -Force; Fail 'second instance did not exit' } else { Ok 'single instance' }
  if ($p.HasExited) { Fail 'first instance died after the second launch' }
}

# ---------------------------------------------------------------- 2. bar (horizontal) layout
$p = Start-App '{ "HorizontalMode": true, "AutoHide": false }'
if ($p) { $h = Panel $p; if ($h) { Shot $h 'panel-bar' 24; Ok 'bar layout' } else { Fail 'bar layout: no panel' } }

# ---------------------------------------------------------------- 3. light theme, 60% opacity, wider
$p = Start-App '{ "Skin": "LightPure_Elegant", "Opacity": 0.6, "PanelWidth": 300, "AutoHide": false, "UIScale": 0.85 }'
if ($p) { $h = Panel $p; if ($h) { Shot $h 'panel-light-60' 24; Ok 'light theme' } else { Fail 'light theme: no panel' } }

# ---------------------------------------------------------------- 4. click-through
$p = Start-App '{ "ClickThrough": true, "AutoHide": false, "UIScale": 0.9 }'
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
$p = Start-App $null @('--open', 'settings:Hardware')
if ($p) { Start-Sleep 5; Window $p 'Settings - WinMonitor' 'settings-hardware' }
$p = Start-App $null @('--open', 'settings:About')
if ($p) { Window $p 'Settings - WinMonitor' 'settings-about' }
$p = Start-App $null @('--open', 'history')
if ($p) { Window $p 'Monitor History' 'history' }
$p = Start-App $null @('--open', 'hardware')
if ($p) { Window $p 'Hardware Info' 'hardware-info' }
$p = Start-App $null @('--open', 'themes')
if ($p) { Window $p 'Theme Editor' 'theme-editor' }
# keep this instance drawing for the PresentMon check below

# ---------------------------------------------------------------- 6. errors
$log = Join-Path $Dir 'WinMonitor_Error.log'
if (Test-Path $log) { Get-Content $log | Select-Object -First 80; Fail 'WinMonitor_Error.log was written' } else { Ok 'no error log' }
Remove-Item "$Dir\settings.json*", "$Dir\*.json.bak", "$Dir\TrafficHistory.json", "$Dir\HardwareHistory.json", $log -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 7. official PresentMon works with WinMonitor's arguments
try {
  $pm = Join-Path $env:RUNNER_TEMP 'PresentMon.exe'
  Invoke-WebRequest 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' -OutFile $pm
  $hash = (Get-FileHash $pm -Algorithm SHA256).Hash
  if ($hash -ne 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF') { Fail "PresentMon hash changed: $hash" }
  $out = Join-Path $env:RUNNER_TEMP 'pm.txt'
  $pp = Start-Process $pm -ArgumentList '--session_name WinMonitor_CI --stop_existing_session --output_stdout' -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
  Start-Sleep 10
  if (-not $pp.HasExited) { Stop-Process $pp -Force }
  $lines = @(Get-Content $out -TotalCount 3)
  Write-Host "PresentMon output ($((Get-Content $out).Count) lines):"; $lines | ForEach-Object { Write-Host "  $_" }
  Write-Host "PresentMon errors:"; Get-Content "$out.err" -TotalCount 20 | ForEach-Object { Write-Host "  $_" }
  if ($lines.Count -eq 0 -or "$($lines[0])" -notmatch '^Application,ProcessID') { Fail 'PresentMon output is not the expected CSV' } else { Ok 'PresentMon CSV' }
  logman stop WinMonitor_CI -ets 2>$null | Out-Null
} catch { Fail "PresentMon test: $_" }
Stop-App

if ($script:failed.Count -gt 0) { Write-Host "`n$($script:failed.Count) check(s) failed:"; $script:failed | ForEach-Object { Write-Host " - $_" }; exit 1 }
Write-Host "`nAll checks passed."
