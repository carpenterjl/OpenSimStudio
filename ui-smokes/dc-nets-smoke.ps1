# UI test — the board-wide "Evaluate DC nets" sweep with a CSV report.
#
# Imports the Breakout_Board IPC-2581 export, opens the Electrical workspace's
# "7. Signal integrity" section, clicks "Evaluate DC nets (R, C, τ → CSV)" (no net
# selection needed — the sweep covers every net with ≥2 pads), drives the SAVE dialog
# to a fresh temp path, and validates the saved CSV (preamble, header, data rows).
#
# Both file dialogs are driven purely by WINDOW HANDLE (WM_SETTEXT + BM_CLICK on the
# dialog-item IDOK) — never SendKeys, which types into whatever window holds focus.
# The OPEN dialog's file-name control is a ComboBoxEx32 (AutomationId 1148, no UIA
# patterns); the vista SAVE dialog's is usually a plain Edit (AutomationId 1001) —
# Get-FileNameEdit probes both shapes.
param(
    [string]$Exe   = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe",
    [string]$Board = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\Breakout_Board.xml"
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Win32Dlg {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, StringBuilder l);
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
# Find the file dialog's file-name EDIT hwnd, either dialog shape:
#  - Open dialog: AutomationId 1148 = ComboBoxEx32 (NO UIA patterns) → ComboBox → Edit.
#  - Save dialog: AutomationId 1001 = a plain Edit control.
# Returns @{ Edit = hwnd; Dialog = UIA window element } or $null.
function Get-FileNameEdit {
    $ctrl = Find-ById $AE::RootElement "1148"
    $edit = [IntPtr]::Zero
    if ($null -ne $ctrl -and $ctrl.Current.ClassName -eq "ComboBoxEx32") {
        $combo = [Win32Dlg]::FindWindowEx([IntPtr]$ctrl.Current.NativeWindowHandle, [IntPtr]::Zero, "ComboBox", $null)
        $edit = [Win32Dlg]::FindWindowEx($combo, [IntPtr]::Zero, "Edit", $null)
    }
    if ($edit -eq [IntPtr]::Zero) {
        $ctrl = Find-ById $AE::RootElement "1001"
        if ($null -ne $ctrl -and $ctrl.Current.ClassName -eq "Edit") {
            $edit = [IntPtr]$ctrl.Current.NativeWindowHandle
        }
    }
    if ($null -eq $ctrl -or $edit -eq [IntPtr]::Zero) { return $null }
    $dlg = $ctrl
    while ($null -ne $dlg -and $dlg.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $dlg = $Walker.GetParent($dlg) }
    if ($null -eq $dlg) { return $null }
    return @{ Edit = $edit; Dialog = $dlg }
}
# Set the path (WM_SETTEXT, readback-verified) and press the default button (IDOK = 1;
# it is Open on an Open dialog, Save on a Save dialog). $tries bounds the total wait —
# the Save dialog only appears after the sweep computes.
function Drive-FileDialog($path, $tries = 20) {
    for ($i = 0; $i -lt $tries; $i++) {
        Start-Sleep -Milliseconds 700
        $found = Get-FileNameEdit
        if ($null -eq $found) { continue }

        [Win32Dlg]::SendMessage($found.Edit, 0x000C, [IntPtr]::Zero, $path) | Out-Null   # WM_SETTEXT
        Start-Sleep -Milliseconds 300
        $sb = New-Object System.Text.StringBuilder 512
        [Win32Dlg]::SendMessage($found.Edit, 0x000D, [IntPtr]512, $sb) | Out-Null        # WM_GETTEXT
        if ($sb.ToString() -ne $path) { return "readback mismatch: '$($sb.ToString())'" }

        $ok = [Win32Dlg]::GetDlgItem([IntPtr]$found.Dialog.Current.NativeWindowHandle, 1) # IDOK
        if ($ok -eq [IntPtr]::Zero) { return "IDOK not found" }
        [Win32Dlg]::SendMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # BM_CLICK

        foreach ($t in 1..10) {
            Start-Sleep -Milliseconds 500
            if ($null -eq (Get-FileNameEdit)) { return "driven (WM_SETTEXT + IDOK)" }
        }
        return "dialog did not close — path rejected?"
    }
    return "file dialog never appeared"
}

$csv = Join-Path $env:TEMP "dcnets-smoke.csv"
if (Test-Path $csv) { Remove-Item $csv -Force }    # fresh path — no overwrite-confirm child dialog

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7
try {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "OpenSim Studio")
    $win = $null
    foreach ($try in 1..10) { $win = $AE::RootElement.FindFirst($TS::Children, $cond); if ($null -ne $win) { break }; Start-Sleep -Seconds 1 }
    if ($null -eq $win) { "FAIL: window not found"; exit 1 }

    "IMPORT: $(Invoke-ButtonLike $win 'Import PCB')"
    "DIALOG: $(Drive-FileDialog $Board)"

    "NAV: $(Invoke-ButtonLike $win 'Electrical')"

    # Wait until the import populated the SI board block (HasBoard shows the button).
    $ready = $false
    foreach ($t in 1..30) {
        Start-Sleep -Seconds 1
        if ($null -ne (Find-ById $win 'SiDcNetsButton')) { $ready = $true; break }
    }
    if (-not $ready) { "FAIL: board import did not reveal SiDcNetsButton"; exit 1 }
    "BOARD: SI board block ready"

    # No net selection needed — the sweep covers every ≥2-pad net on the board.
    "SWEEP: $(Invoke-ById $win 'SiDcNetsButton')"
    "SAVE: $(Drive-FileDialog $csv -tries 90)"     # the dialog appears after the sweep computes

    $r = Wait-Text $win "net\(s\) evaluated|not computable|Not computable" 40
    "RESULT: $r"

    if (-not (Test-Path $csv)) { "FAIL: CSV not written"; exit 1 }
    $lines = Get-Content $csv
    $header = $lines | Where-Object { $_ -like "Net,Pad A,Part A,Pad B,Part B*" }
    $data = @($lines | Where-Object { $_ -notmatch '^#' -and $_ -notlike "Net,Pad A*" -and $_.Trim() -ne '' })
    if ($null -eq $header) { "FAIL: CSV header missing"; exit 1 }
    if (-not ($lines | Where-Object { $_ -like "# board:*" })) { "FAIL: board preamble line missing"; exit 1 }
    # A named pad row proves the PinRef/Component identity made it end to end.
    $named = @($data | Where-Object { $_ -match '^[^,]*,[A-Z]+\d+\.\d+,' })
    "CSV: $($data.Count) data row(s), $($named.Count) with refdes.pin pads; first named: $($named | Select-Object -First 1)"
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    if (Test-Path $csv) { Remove-Item $csv -Force }
}
"done"
