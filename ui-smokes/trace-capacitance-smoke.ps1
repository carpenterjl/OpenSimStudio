# UI test — per-trace capacitance to ground on a real PCB.
#
# Imports the Breakout_Board IPC-2581 export (named nets), opens the Electrical
# workspace's "7. Signal integrity" section, ticks a communication net (I2C _SCL,
# with fallbacks) in the board-nets list, clicks "Per-trace C to ground", and expects
# a "C ≈ … pF to ground" result line — the net's total capacitance over the stackup
# dielectric (Σ C′·length + pad plate terms).
#
# Drives the app purely through UI Automation (no test hooks); the file dialog is
# driven by ValuePattern on the File-name control + the Open button — never SendKeys,
# which types into whatever happens to hold focus. Self-bounded; prints a RESULT line.
param(
    [string]$Exe   = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe",
    [string]$Board = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\Breakout_Board.xml"
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Dlg {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr dlg, int id);
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
# Drive the Open dialog entirely by WINDOW HANDLE — never by keyboard focus. The
# common dialog's File-name control (AutomationId 1148, class ComboBoxEx32) exposes
# NO UIA patterns (probed live: empty pattern list), and SendKeys types into whatever
# window happens to be foreground (it hit the wrong window once). So: WM_SETTEXT to
# the ComboBoxEx32's Edit hwnd (verified by WM_GETTEXT readback), then BM_CLICK to
# GetDlgItem(dialog, IDOK=1) — a UIA search for AutomationId "1" is ambiguous (it
# matched a file ListItem live), the dialog-item id is not.
function Get-FileNameControl {
    $byId = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "1148")
    return $AE::RootElement.FindFirst($TS::Descendants, $byId)
}
function Open-FileDialog($path) {
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 700
        $ctrl = Get-FileNameControl
        if ($null -eq $ctrl) { continue }

        $comboEx = [IntPtr]$ctrl.Current.NativeWindowHandle
        $combo = [Win32Dlg]::FindWindowEx($comboEx, [IntPtr]::Zero, "ComboBox", $null)
        $edit = [Win32Dlg]::FindWindowEx($combo, [IntPtr]::Zero, "Edit", $null)
        if ($edit -eq [IntPtr]::Zero) { return "file-name Edit hwnd not found" }
        [Win32Dlg]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $path) | Out-Null   # WM_SETTEXT
        Start-Sleep -Milliseconds 300

        $dlg = $ctrl
        while ($null -ne $dlg -and $dlg.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $dlg = $Walker.GetParent($dlg) }
        if ($null -eq $dlg) { return "dialog window not found above the file-name control" }
        $ok = [Win32Dlg]::GetDlgItem([IntPtr]$dlg.Current.NativeWindowHandle, 1)    # IDOK
        if ($ok -eq [IntPtr]::Zero) { return "Open button (IDOK) not found" }
        [Win32Dlg]::SendMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # BM_CLICK

        # Confirm the dialog actually closed (a bad path leaves it up).
        foreach ($t in 1..10) {
            Start-Sleep -Milliseconds 500
            if ($null -eq (Get-FileNameControl)) { return "opened (WM_SETTEXT + IDOK)" }
        }
        return "dialog did not close — path rejected?"
    }
    return "file dialog not driven"
}
# Tick a net's CheckBox inside the SiBoardNetsList by label substring. The ListBox
# VIRTUALIZES: only realized (on-screen) items exist in the UIA tree, so scroll the
# list page by page via its ScrollPattern and search the realized items at each stop.
function Check-NetInList($root, $listId, $needle) {
    $list = Find-ById $root $listId; if ($null -eq $list) { return $null }
    $sp = $null
    try { $sp = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch {}
    if ($null -ne $sp -and $sp.Current.VerticallyScrollable) {
        $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
        Start-Sleep -Milliseconds 200
    }
    for ($page = 0; $page -lt 200; $page++) {
        foreach ($cb in $list.FindAll($TS::Descendants, (TypeCond ([System.Windows.Automation.ControlType]::CheckBox)))) {
            if ($cb.Current.Name -like "*$needle*") {
                $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
                return $cb.Current.Name
            }
        }
        if ($null -eq $sp -or -not $sp.Current.VerticallyScrollable) { break }
        if ($sp.Current.VerticalScrollPercent -ge 100) { break }
        $sp.ScrollVertical([System.Windows.Automation.ScrollAmount]::LargeIncrement)
        Start-Sleep -Milliseconds 150
    }
    return $null
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

    "NAV: $(Invoke-ButtonLike $win 'Electrical')"

    # Wait until the import has actually populated the SI board-nets list (HasBoard).
    $listReady = $false
    foreach ($t in 1..30) {
        Start-Sleep -Seconds 1
        $list = Find-ById $win 'SiBoardNetsList'
        if ($null -ne $list) { $listReady = $true; break }
    }
    if (-not $listReady) { "FAIL: board import did not populate SiBoardNetsList"; exit 1 }
    "BOARD: net list populated"

    # Tick a communication net in the SI board-nets list. Match WITHOUT the leading
    # underscore: a WPF CheckBox whose Content string starts with '_' treats it as an
    # ACCESS-KEY marker, so the UIA Name of net '_SCL' reads 'SCL' (found live).
    $commsNets = @('SCL', 'SDA', 'MISO', 'MOSI', 'SERIAL_TX', 'SERIAL_RX')
    $picked = $null
    foreach ($needle in $commsNets) {
        $name = Check-NetInList $win 'SiBoardNetsList' $needle
        if ($null -ne $name) { $picked = $name; break }
        "NET '$needle': not in list"
    }
    if ($null -eq $picked) { "FAIL: no communication net found in SiBoardNetsList"; exit 1 }
    "NET: ticked '$picked'"

    "CAP: $(Invoke-ById $win 'SiTraceCapButton')"
    $r = Wait-Text $win "pF to ground|not computable|Not computable" 40
    "RESULT: $r"
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
"done"
