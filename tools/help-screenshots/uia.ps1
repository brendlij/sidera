Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$script:proc = $null
$script:win = $null

function Start-Sidera {
    $exe = Join-Path $PSScriptRoot "..\..\src\Sidera.Desktop\bin\Release\net10.0-windows\Sidera.Desktop.exe"
    $script:proc = Start-Process -FilePath $exe -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $script:win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:proc.Id)))
        if ($script:win) { return }
    }
    throw "Window not found"
}

function Stop-Sidera {
    if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill() }
}

function All-Elements {
    for ($i = 0; $i -lt 10; $i++) {
        try { return $script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw "UIA tree unavailable"
}

function Find-ByName($name, $type = $null) {
    foreach ($e in (All-Elements)) {
        if ($e.Current.Name -eq $name) {
            if (-not $type -or $e.Current.ControlType.ProgrammaticName -eq $type) { return $e }
        }
    }
    return $null
}

function Find-ByNameStarts($prefix) {
    foreach ($e in (All-Elements)) { if ($e.Current.Name -like "$prefix*") { return $e } }
    return $null
}

function Click-Button($name) {
    $e = Find-ByName $name "ControlType.Button"
    if (-not $e) { throw "Button '$name' not found" }
    if (-not $e.Current.IsEnabled) { throw "Button '$name' is disabled" }
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Button-Enabled($name) {
    $e = Find-ByName $name "ControlType.Button"
    if (-not $e) { return "missing" }
    return $e.Current.IsEnabled
}

function Dump-Tree($filter = "") {
    foreach ($e in (All-Elements)) {
        $c = $e.Current
        $line = "{0,-28} | {1,-40} | id={2} | en={3}" -f $c.ControlType.ProgrammaticName.Replace("ControlType.", ""), $c.Name, $c.AutomationId, $c.IsEnabled
        if ($filter -eq "" -or $line -like "*$filter*") { $line }
    }
}

function Get-Edits {
    $list = @()
    foreach ($e in (All-Elements)) {
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.Edit") { $list += $e }
    }
    return $list
}

function Set-Text($element, $text) {
    $vp = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($text)
}

function Get-Text($element) {
    $vp = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $vp.Current.Value
}

function Select-Item($name) {
    foreach ($e in (All-Elements)) {
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.ListItem" -and $e.Current.Name -like "$name*") {
            $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            return $true
        }
    }
    return $false
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeMouse {
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04;
}
"@

function Click-At($element) {
    $r = $element.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [NativeMouse]::SetForegroundWindow([IntPtr]$script:win.Current.NativeWindowHandle) | Out-Null
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
    Start-Sleep -Milliseconds 80
    [NativeMouse]::mouse_event([NativeMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    [NativeMouse]::mouse_event([NativeMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}
