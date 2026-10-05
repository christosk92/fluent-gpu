# Background e2e helpers for the Wavee UI driver (PowerShell 5.1). Dot-source this file.
# Rules: never moves the real cursor, never sends real keys, never activates/foregrounds a window, only touches windows of the
# Wavee process this run started. Input reaches the app through the engine's `--fg test-input` private window message.
Add-Type -AssemblyName System.Drawing
if (-not ('E2E.W' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging;
namespace E2E {
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref System.Drawing.Point p);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP f, IntPtr l);
  public static uint TI = RegisterWindowMessage("FluentGpu.TestInput");
  public static List<IntPtr> Wins(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static string Info(IntPtr h) { var c = new StringBuilder(128); GetClassName(h, c, 128); var t = new StringBuilder(256); GetWindowText(h, t, 256);
    RECT r; GetWindowRect(h, out r); return h.ToInt64() + "|" + c + "|" + t + "|" + r.L + "," + r.T + "," + (r.R - r.L) + "," + (r.B - r.T); }
  public static IntPtr LP(int x, int y) { return (IntPtr)(long)(((uint)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF)); }
  public static void TIPost(IntPtr h, int kind, int x, int y, int notch) { PostMessage(h, TI, (IntPtr)(long)(((uint)(notch & 0xFFFF) << 16) | (uint)kind), LP(x, y)); }
  // Captures the window with PrintWindow(PW_RENDERFULLCONTENT); a scale above 1 (the window sits on a scaled monitor) is divided out so every
  // screenshot is in the same 100 percent layout pixels the driver's constants use.
  public static void Shot(IntPtr h, string png, double scale) {
    RECT r; GetWindowRect(h, out r); int w = r.R - r.L, hh = r.B - r.T;
    using (var bmp = new Bitmap(w, hh, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      if (scale > 1.001) {
        using (var sm = new Bitmap((int)Math.Round(w / scale), (int)Math.Round(hh / scale), PixelFormat.Format32bppArgb)) {
          using (var g2 = Graphics.FromImage(sm)) { g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g2.DrawImage(bmp, 0, 0, sm.Width, sm.Height); }
          sm.Save(png, ImageFormat.Png);
        }
      } else bmp.Save(png, ImageFormat.Png);
    }
  }
  // Region stats of a PNG: mean luma, luma stddev, count of distinct quantized colors (video test pattern = many; flat UI = few), share of near-black pixels.
  public static double[] Stats(string png, int x, int y, int w, int h) {
    using (var b = new Bitmap(png)) {
      x = Math.Max(0, x); y = Math.Max(0, y); w = Math.Min(w, b.Width - x); h = Math.Min(h, b.Height - y);
      if (w <= 0 || h <= 0) return new double[] { 0, 0, 0, 0 };
      double s = 0, s2 = 0; long n = 0, dark = 0; var set = new HashSet<int>();
      for (int j = y; j < y + h; j += 3) for (int i = x; i < x + w; i += 3) { var c = b.GetPixel(i, j); double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; s += l; s2 += l * l; n++; if (l < 8) dark++; set.Add(((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4)); }
      double m = s / n; return new double[] { m, Math.Sqrt(Math.Max(0, s2 / n - m * m)), set.Count, (double)dark / n };
    }
  }
}}
'@
}
[E2E.W]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
function Rect($h) { $r = New-Object E2E.W+RECT; [E2E.W]::GetWindowRect($h, [ref]$r) | Out-Null; [pscustomobject]@{ X = $r.L; Y = $r.T; W = $r.R - $r.L; H = $r.B - $r.T } }
function ClientRect($h) { $r = New-Object E2E.W+RECT; [E2E.W]::GetClientRect($h, [ref]$r) | Out-Null; [pscustomobject]@{ W = $r.R; H = $r.B } }
function WinsOf($procId) { [E2E.W]::Wins([uint32]$procId) | ForEach-Object { $i = [E2E.W]::Info($_).Split('|'); [pscustomobject]@{ H = $_; Class = $i[1]; Title = $i[2]; Rect = $i[3] } } }
function Place($h, $x, $y, $w, $hh) { [E2E.W]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, $w, $hh, 0x0014) | Out-Null }   # NOZORDER|NOACTIVATE
function Unminimize($h) { if ([E2E.W]::IsIconic($h)) { [E2E.W]::ShowWindow($h, 4) | Out-Null } }                          # SW_SHOWNOACTIVATE
$script:S = 1.0
function Shot($h, $png) { Unminimize $h; [E2E.W]::Shot($h, $png, $script:S) }
# Coordinates given to the input helpers are WINDOW pixels (as in a PrintWindow screenshot); the message wants CLIENT pixels, so the
# window-to-client origin (the invisible resize border) is subtracted per call.
function Co($h) { $p = New-Object System.Drawing.Point(0, 0); [E2E.W]::ClientToScreen($h, [ref]$p) | Out-Null; $r = Rect $h; [pscustomobject]@{ X = $p.X - $r.X; Y = $p.Y - $r.Y } }
function Move-To($h, $x, $y) { $o = Co $h; [E2E.W]::TIPost($h, 0, [int]($x * $script:S - $o.X), [int]($y * $script:S - $o.Y), 0) }
function Down($h, $x, $y) { $o = Co $h; [E2E.W]::TIPost($h, 1, [int]($x * $script:S - $o.X), [int]($y * $script:S - $o.Y), 0) }
function Up($h, $x, $y) { $o = Co $h; [E2E.W]::TIPost($h, 2, [int]($x * $script:S - $o.X), [int]($y * $script:S - $o.Y), 0) }
function Leave($h) { [E2E.W]::TIPost($h, 3, 0, 0, 0) }
function Wheel($h, $x, $y, $notch) { $o = Co $h; [E2E.W]::TIPost($h, 4, [int]($x * $script:S - $o.X), [int]($y * $script:S - $o.Y), [int]$notch) }
function Click($h, $x, $y) { Move-To $h $x $y; Start-Sleep -Milliseconds 60; Down $h $x $y; Start-Sleep -Milliseconds 70; Up $h $x $y }
function Hover($h, $x, $y, $steps = 8, $fx = 5, $fy = 5) { for ($i = 1; $i -le $steps; $i++) { Move-To $h ($fx + ($x - $fx) * $i / $steps) ($fy + ($y - $fy) * $i / $steps); Start-Sleep -Milliseconds 16 } }
function Nchit($h, $sx, $sy) { $r = [E2E.W]::SendMessage($h, 0x84, [IntPtr]::Zero, [E2E.W]::LP($sx, $sy)); [int64]$r }
function RegionStats($png, $x, $y, $w, $hh) { $s = [E2E.W]::Stats($png, $x, $y, $w, $hh); [pscustomobject]@{ Mean = [math]::Round($s[0], 1); Std = [math]::Round($s[1], 1); Colors = [int]$s[2]; Dark = [math]::Round($s[3], 3) } }
function Start-Wavee($exe, $profileDir, $clip) {
  if (Test-Path $profileDir) { Remove-Item -Recurse -Force $profileDir }
  New-Item -ItemType Directory -Force $profileDir | Out-Null
  $p = Start-Process -FilePath $exe -ArgumentList @('--fake', '--profile', "`"$profileDir`"", '--fake-video', "`"$clip`"", '--fg', 'test-input') -PassThru -WindowStyle Minimized
  $p
}
function Main-Hwnd($procId) { for ($i = 0; $i -lt 60; $i++) { $w = WinsOf $procId | ? Class -eq 'FluentGpuWindow' | Select-Object -First 1; if ($w) { return $w.H }; Start-Sleep -Milliseconds 250 }; $null }
function Log-Path($profileDir) { (Get-ChildItem (Join-Path $profileDir 'logs') -Filter 'wavee-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName }
# Restore (no activation) and place on the upper monitor. The window is requested at BASE size (layout px at 100 percent); once it sits on the
# monitor its DPI scale S is read and the physical size is base * S, so every later constant stays in base px. Returns the final rect.
function Settle-Window($h, $x, $y, $w, $hh) {
  Unminimize $h; Start-Sleep -Milliseconds 800
  Place $h $x $y $w $hh; Start-Sleep -Milliseconds 900
  for ($i = 0; $i -lt 8; $i++) {
    $script:S = [E2E.W]::GetDpiForWindow($h) / 96.0 / 1.25   # base px = layout calibrated at 125 percent DPI
    $pw = [int]($w * $script:S); $ph = [int]($hh * $script:S)
    Place $h $x $y $pw $ph; Start-Sleep -Milliseconds 900
    $r = Rect $h; $s2 = [E2E.W]::GetDpiForWindow($h) / 96.0 / 1.25
    if ($s2 -eq $script:S -and $r.W -eq $pw -and $r.H -eq $ph -and $r.X -eq $x -and $r.Y -eq $y) { break }
  }
  Rect $h
}
# Does the foreground window belong to this process? (Read-only: only the owning pid of the foreground window is looked at.)
function Foreground-IsMine($procId) { $fg = [E2E.W]::GetForegroundWindow(); [uint32]$p = 0; [E2E.W]::GetWindowThreadProcessId($fg, [ref]$p) | Out-Null; ($p -eq $procId) }
