# UI test — communication-trace RF field overlay on a real PCB.
#
# Imports the Breakout_Board IPC-2581 export (which carries NAMED nets), picks a
# communication trace (I2C _SCL / _SDA, or UART _SERIAL_TX/_RX — "like SPI"), runs the
# thin-wire MoM RF simulation on that net, and paints the radiated |E| as a translucent
# per-copper-layer heatmap over the board outline (Stage S10 board overlay). Then it
# MAXIMIZES the window and saves a screenshot of the viewport with the overlay visible.
#
# Drives the app purely through UI Automation (no test hooks) — the file dialog is driven
# by keyboard (see memory uia-file-dialog-technique). Self-bounded; leaves a PNG + a
# one-line RESULT for the caller to inspect.
param(
    [string]$Exe   = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe",
    [string]$Board = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\Breakout_Board.xml",
    [string]$Shot  = "E:\Temp_Folder\Appdata\Local\Temp\claude\C--Users-Carpe-Desktop-Claude-App-Tests-OpenSimStudio\9445252c-c6ea-4580-9cc3-39ca8c16e861\scratchpad\spi-board-overlay.png"
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$Walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function TypeCond($type) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type) }
function Find-ById($root, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $c)
}
function Invoke-ById($root, $id) {
    $b = Find-ById $root $id; if ($null -eq $b) { return "MISSING $id" }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "clicked $id"
}
function Invoke-ButtonLike($root, $needle) {
    foreach ($b in $root.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::Button)))) {
        if ($b.Current.IsOffscreen) { continue }
        $hit = $b.Current.Name -like "*$needle*"
        if (-not $hit) { foreach ($t in $b.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::Text)))) { if ($t.Current.Name -like "*$needle*") { $hit=$true; break } } }
        if ($hit) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "clicked '$needle'" }
    }
    return "MISSING button '*$needle*'"
}
function Get-TextMatching($root, $pattern) {
    foreach ($t in $root.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::Text)))) {
        if ($t.Current.Name -match $pattern) { return $t.Current.Name }
    }
    return $null
}
function Wait-Text($root, $pattern, $tries = 40) {
    foreach ($i in 1..$tries) { Start-Sleep -Seconds 1; $t = Get-TextMatching $root $pattern; if ($null -ne $t) { return $t } }
    return $null
}
function Check-ById($root, $id) {
    $b = Find-ById $root $id; if ($null -eq $b) { return "MISSING $id" }
    $tp = $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
    return "checked $id"
}
function Set-ById($root, $id, $v) {
    $b = Find-ById $root $id; if ($null -eq $b) { return "MISSING $id" }
    $b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v); return "set $id=$v"
}
function Select-ComboItemLike($root, $comboId, $needle) {
    $cb = Find-ById $root $comboId; if ($null -eq $cb) { return $null }
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700
    foreach ($it in $cb.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::ListItem)))) {
        if ($it.Current.Name -like "*$needle*") {
            $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
            return $it.Current.Name
        }
    }
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); return $null
}
function Get-DialogWindow {
    $byId = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "1148")
    $ctrl = $AE::RootElement.FindFirst($TS::Descendants, $byId)
    if ($null -eq $ctrl) { return $null }
    $anc = $ctrl
    while ($null -ne $anc -and $anc.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $anc = $Walker.GetParent($anc) }
    return $anc
}
function Open-FileDialog($path) {
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 700
        $dlg = Get-DialogWindow
        if ($null -eq $dlg) { continue }
        try { $dlg.SetFocus() } catch {}
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait("$path{ENTER}")
        return "opened (keyboard)"
    }
    return "file dialog not driven"
}
# Right-click the viewport to open its context menu, then pick View -> Top so the overlay
# reads as a flat top-down PCB map. The menu is a top-level popup (search from RootElement).
function Set-TopView($win) {
    $h = [IntPtr]$win.Current.NativeWindowHandle
    [Win32]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 500
    $r = $win.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width * 0.42); $y = [int]($r.Y + $r.Height * 0.45)  # over the board
    [Win32]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 300
    [Win32]::mouse_event(0x08, 0, 0, 0, [IntPtr]::Zero)   # RIGHTDOWN
    [Win32]::mouse_event(0x10, 0, 0, 0, [IntPtr]::Zero)   # RIGHTUP
    Start-Sleep -Milliseconds 800
    $viewItem = $null
    foreach ($i in 1..10) {
        foreach ($mi in $AE::RootElement.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::MenuItem)))) {
            if ($mi.Current.Name -eq "View") { $viewItem = $mi; break }
        }
        if ($null -ne $viewItem) { break }; Start-Sleep -Milliseconds 300
    }
    if ($null -eq $viewItem) { return "View menu not found" }
    $viewItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
    foreach ($mi in $AE::RootElement.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::MenuItem)))) {
        if ($mi.Current.Name -eq "Top") {
            $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            return "top view set"
        }
    }
    return "Top item not found"
}
function Save-Screenshot($win, $path) {
    try {
        $h = [IntPtr]$win.Current.NativeWindowHandle
        [Win32]::ShowWindow($h, 3) | Out-Null   # SW_MAXIMIZE
        [Win32]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Seconds 2
        $r = $win.Current.BoundingRectangle
        $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        return "saved $path ($([int]$r.Width)x$([int]$r.Height))"
    } catch { return "screenshot failed: $_" }
}

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7
try {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "OpenSim Studio")
    $win = $null
    foreach ($try in 1..10) { $win = $AE::RootElement.FindFirst($TS::Children, $cond); if ($null -ne $win) { break }; Start-Sleep -Seconds 1 }
    if ($null -eq $win) { "FAIL: window not found"; exit 1 }

    "IMPORT: $(Invoke-ButtonLike $win 'Import PCB')"
    "DIALOG: $(Open-FileDialog $Board)"
    Start-Sleep -Seconds 10   # IPC-2581 parse + net extraction + preview build

    "NAV: $(Invoke-ButtonLike $win 'Electrical')"
    Start-Sleep -Seconds 3

    # Antenna source = the selected board net (thin-wire trace-chain MoM).
    "SOURCE: $(Select-ComboItemLike $win 'AntennaSourceCombo' 'Selected net')"
    Start-Sleep -Milliseconds 500
    # A slightly higher frequency shows more field structure over the board.
    "FREQ: $(Set-ById $win 'FrequencyBox' '900')"

    # Try communication traces in priority order until one composes into a solvable chain.
    $commsNets = @('_SCL', '_SDA', '_SERIAL_TX', '_SERIAL_RX', '_TX1_P', '_RX1_P', '_TX2_P')
    $result = $null; $picked = $null
    foreach ($needle in $commsNets) {
        $name = Select-ComboItemLike $win 'AntennaNetCombo' $needle
        if ($null -eq $name) { "NET '$needle': not in list"; continue }
        Start-Sleep -Milliseconds 500
        "OVER BOARD: $(Check-ById $win 'OverlayOverBoardCheck')"
        "OVERLAY ($name): $(Invoke-ById $win 'FieldOverlayButton')"
        $r = Wait-Text $win "Board overlay at |Not computable|outside the board" 40
        "  -> $r"
        if ($r -match "Board overlay at ") { $result = $r; $picked = $name; break }
    }

    if ($null -ne $result) {
        "PICKED NET: $picked"
        "RESULT: $result"
        Start-Sleep -Seconds 1
        # Maximize first so the right-click lands over the (larger) board, then top-down view.
        [Win32]::ShowWindow([IntPtr]$win.Current.NativeWindowHandle, 3) | Out-Null
        Start-Sleep -Seconds 2
        "TOP VIEW: $(Set-TopView $win)"
        Start-Sleep -Seconds 2
        "SCREENSHOT: $(Save-Screenshot $win $Shot)"
    } else {
        "RESULT: no communication net composed into a solvable overlay"
    }
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
"done"
