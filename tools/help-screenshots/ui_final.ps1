param([string]$Setup = "two", [string]$Steps = "", [string]$Prefix = "u", [switch]$Keep, [switch]$NoMaximize)
. (Join-Path $PSScriptRoot "uia.ps1")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$shots = if ($env:SIDERA_SHOTS) { $env:SIDERA_SHOTS } else { Join-Path $env:TEMP "sidera-help-shots" }
if (-not (Test-Path $shots)) { New-Item -ItemType Directory -Path $shots | Out-Null }
$dir = Join-Path $env:TEMP "sidera-uifinal-$Setup"
if ((Test-Path $dir) -and -not $Keep) { Remove-Item $dir -Recurse -Force }
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$two = @'
{
  "format": "astra-equipment",
  "version": 1,
  "devices": [
    { "id": "camera.main", "name": "ASI2600MM", "type": "Camera", "backend": "Simulator" },
    { "id": "camera.wide", "name": "ASI533MC", "type": "Camera", "backend": "Simulator" },
    { "id": "focuser.main", "name": "EAF", "type": "Focuser", "backend": "Simulator" },
    { "id": "wheel.main", "name": "EFW", "type": "FilterWheel", "backend": "Simulator" },
    { "id": "mount.am3", "name": "AM3", "type": "Mount", "backend": "Simulator" },
    { "id": "guider.phd2", "name": "PHD2", "type": "Guider", "backend": "Simulator" }
  ],
  "rigs": [
    { "id": "rig.main", "name": "Main 750", "cameraId": "camera.main", "focuserId": "focuser.main", "filterWheelId": "wheel.main", "mountId": "mount.am3", "guiderId": "guider.phd2", "optics": { "focalLengthMm": 750, "apertureMm": 150 } },
    { "id": "rig.wide", "name": "Wide 400", "cameraId": "camera.wide", "mountId": "mount.am3", "guiderId": "guider.phd2", "optics": { "focalLengthMm": 400, "apertureMm": 80 } }
  ]
}
'@
$one = @'
{
  "format": "astra-equipment",
  "version": 1,
  "devices": [
    { "id": "camera.main", "name": "ASI2600MM", "type": "Camera", "backend": "Simulator" },
    { "id": "mount.am3", "name": "AM3", "type": "Mount", "backend": "Simulator" },
    { "id": "guider.phd2", "name": "PHD2", "type": "Guider", "backend": "Simulator" }
  ],
  "rigs": []
}
'@
$none = @'
{
  "format": "astra-equipment",
  "version": 1,
  "devices": [
    { "id": "camera.a", "name": "ASI2600MM", "type": "Camera", "backend": "Simulator" },
    { "id": "camera.b", "name": "ASI533MC", "type": "Camera", "backend": "Simulator" },
    { "id": "mount.am3", "name": "AM3", "type": "Mount", "backend": "Simulator" }
  ],
  "rigs": []
}
'@
$equipment = switch ($Setup) { "one" { $one } "none" { $none } default { $two } }
Set-Content -Path "$dir\equipment.json" -Value $equipment -Encoding UTF8
$env:SIDERA_EQUIPMENT_FILE = "$dir\equipment.json"
$env:SIDERA_SETTINGS_FILE = "$dir\settings.json"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinShot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@
function Shot($name) {
    # The window is drawn into a bitmap by itself: it needs neither the focus nor to be in front of anything (a game may be).
    Start-Sleep -Milliseconds 300
    $h = [IntPtr]$script:win.Current.NativeWindowHandle
    $rect = New-Object WinShot+RECT
    [WinShot]::GetWindowRect($h, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $ht = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [WinShot]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $bmp.Save("$shots\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "shot $name"
}
function Find-ById($id) {
    foreach ($e in (All-Elements)) { if ($e.Current.AutomationId -eq $id) { return $e } }
    return $null
}

$ErrorActionPreference = "Stop"
try {
    Start-Astra
    Start-Sleep -Seconds 2
    $wp = $script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    if (-not $NoMaximize) { $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized) }
    Start-Sleep -Seconds 1
    # Connect everything first, as a person at the telescope would (the Equipment page has a connect for each device; the simulators connect from the dashboard of the demo).
    $n = 0
    foreach ($step in ($Steps -split "\|")) {
        if ($step.Length -eq 0) { continue }
        $n++
        $kind, $arg = $step.Split(":", 2)
        try {
            switch ($kind) {
                "btn" { Click-Button $arg; Start-Sleep -Milliseconds 900 }
                "id" { $e = Find-ById $arg; if (-not $e) { throw "id $arg not found" }; $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900 }
                                "combo" {
                    $id, $item = $arg.Split("=", 2)
                    $e = Find-ById $id
                    if (-not $e) { throw "combo $id not found" }
                    $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
                    if (-not (Select-Item $item)) { throw "item $item not found" }
                    Start-Sleep -Milliseconds 800
                }
                "combon" {
                    $name, $item = $arg.Split("=", 2)
                    $e = Find-ByName $name
                    if (-not $e) { throw "combo named $name not found" }
                    $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
                    if (-not (Select-Item "*$item")) { throw "item $item not found" }
                    Start-Sleep -Milliseconds 800
                }
                "selname" {
                    $e = Find-ByNameStarts $arg
                    if (-not $e) { throw "item starting with $arg not found" }
                    $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900
                }
                "edit" {
                    $id, $value = $arg.Split("=", 2)
                    $e = Find-ById $id
                    if (-not $e) { throw "edit $id not found" }
                    Set-Text $e $value; Start-Sleep -Milliseconds 500
                }
                "radio" {
                    $e = Find-ByName $arg "ControlType.RadioButton"
                    if (-not $e) { throw "radio $arg not found" }
                    $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900
                }
                "sleep" { Start-Sleep -Milliseconds ([int]$arg) }
                "shot" { Shot ($Prefix + "_" + $arg) }
                "dump" { Dump-Tree $arg }
                default { "unknown step $step" }
            }
        } catch { "step $n '$step' failed: $_" }
    }
}
finally {
    Stop-Astra
}
