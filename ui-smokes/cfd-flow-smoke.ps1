# UI test — conjugate heat flow with computed airflow (Phase 6 Stage 2, CFD).
#
# Imports the three-part STEP assembly, meshes it, sets a MOVING-fluid environment, and
# runs the "Heat flow with computed airflow (CFD)" analysis: voxelize → laminar flow +
# fluid energy solve → wall film → frozen-flow solid transient. Then exercises the flow
# panel (summary line, arrows/streamlines/slice toggles) and proves the timeline still
# appears (the solid leg is a normal multi-frame transient) and that Mechanical carries
# no CFD control.
#
# Inherits every UIA trap of thermal-flow-smoke.ps1 (no click-by-text, Border has no
# peer, LogService "[HH:mm:ss] " prefix, LostFocus boxes need a focus nudge, hwnd-driven
# file dialog). CFD-specific choices:
#  - The fixture's parts are 1 mm cubes spanning 21 mm, so the CELL SIZE IS SET
#    EXPLICITLY (1 mm): the automatic size (smallest extent / 24) would build a
#    near-budget grid of the 2L/5L auto domain and the smoke would run for ages.
#  - Moving fluid at 0.2 m/s: a still fluid solves the buoyant plume, which converges
#    far slower than a through-flow at smoke-sized grids.
param(
    [string]$Exe  = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe",
    [string]$Step = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\ui-smokes\assembly-three-parts.step"
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
$CT = [System.Windows.Automation.ControlType]
$Walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$failures = New-Object System.Collections.Generic.List[string]

function TypeCond($type) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type) }
function Find-ById($root, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $c)
}
function Wait-ById($root, $id, $tries = 30) {
    foreach ($i in 1..$tries) {
        $e = Find-ById $root $id
        if ($null -ne $e -and -not $e.Current.IsOffscreen) { return $e }
        Start-Sleep -Seconds 1
    }
    return $null
}
function Invoke-ById($root, $id) {
    $b = Find-ById $root $id; if ($null -eq $b) { return "MISSING $id" }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "clicked $id"
}
function Set-Value($e, $v) {
    if ($null -eq $e) { return "MISSING box" }
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v); return $v
}
function Get-Name($e) { if ($null -eq $e) { return $null } return $e.Current.Name }
function Set-Check($e, [bool]$on) {
    if ($null -eq $e) { return "MISSING check" }
    $p = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $want = if ($on) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    $guard = 0
    while ($p.Current.ToggleState -ne $want -and $guard -lt 3) { $p.Toggle(); $guard++; Start-Sleep -Milliseconds 150 }
    return $p.Current.ToggleState
}
function Select-ComboItem($root, $id, $needle) {
    $combo = Find-ById $root $id
    if ($null -eq $combo) { return "MISSING combo $id" }
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 400
    foreach ($it in $combo.FindAll($TS::Descendants, (TypeCond ($CT::ListItem)))) {
        $hit = $it.Current.Name -like "*$needle*"
        if (-not $hit) {
            foreach ($t in $it.FindAll($TS::Descendants, (TypeCond ($CT::Text)))) {
                if ($t.Current.Name -like "*$needle*") { $hit = $true; break }
            }
        }
        if ($hit) {
            $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 250
            $ec.Collapse()
            return "selected '$needle' in $id"
        }
    }
    $ec.Collapse()
    return "MISSING item '*$needle*' in $id"
}
function Get-ComboSelection($root, $id) {
    $combo = Find-ById $root $id
    if ($null -eq $combo) { return $null }
    $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    return (($sel | ForEach-Object { $_.Current.Name }) -join '')
}
function Get-TextMatching($root, $pattern) {
    foreach ($t in $root.FindAll($TS::Descendants, (TypeCond ($CT::Text)))) {
        if ($t.Current.Name -match $pattern) { return $t.Current.Name }
    }
    return $null
}
function Get-LogMatching($root, $pattern) {
    $log = Find-ById $root 'LogList'
    if ($null -eq $log) { return $null }
    foreach ($it in $log.FindAll($TS::Descendants, (TypeCond ($CT::ListItem)))) {
        if ($it.Current.Name -match $pattern) { return $it.Current.Name }
    }
    return $null
}
function Wait-Log($root, $pattern, $tries = 60) {
    foreach ($i in 1..$tries) { $t = Get-LogMatching $root $pattern; if ($null -ne $t) { return $t }; Start-Sleep -Seconds 1 }
    return $null
}
# The log list VIRTUALIZES: after a solve dumps dozens of lines at once, the early ones
# (voxelization, Reynolds number, assumptions) are scrolled out of the realized items and
# Get-LogMatching can never see them (found live). This walks the scroll range in
# overlapping steps and matches at each stop.
function Find-LogAnywhere($root, $pattern) {
    $log = Find-ById $root 'LogList'
    if ($null -eq $log) { return $null }
    $hit = $null
    try {
        $sp = $log.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        foreach ($pct in 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100) {
            $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct)
            Start-Sleep -Milliseconds 150
            $t = Get-LogMatching $root $pattern
            if ($null -ne $t) { $hit = $t; break }
        }
        $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    }
    catch { $hit = Get-LogMatching $root $pattern }   # list too short to scroll
    return $hit
}
function Check($ok, $label) {
    if ($ok) { "PASS: $label" } else { $script:failures.Add($label); "FAIL: $label" }
}
function Get-FileNameEdit {
    $ctrl = Find-ById $AE::RootElement "1148"
    $edit = [IntPtr]::Zero
    if ($null -ne $ctrl -and $ctrl.Current.ClassName -eq "ComboBoxEx32") {
        $combo = [Win32Dlg]::FindWindowEx([IntPtr]$ctrl.Current.NativeWindowHandle, [IntPtr]::Zero, "ComboBox", $null)
        $edit = [Win32Dlg]::FindWindowEx($combo, [IntPtr]::Zero, "Edit", $null)
    }
    if ($edit -eq [IntPtr]::Zero) {
        $ctrl = Find-ById $AE::RootElement "1001"
        if ($null -ne $ctrl -and $ctrl.Current.ClassName -eq "Edit") { $edit = [IntPtr]$ctrl.Current.NativeWindowHandle }
    }
    if ($null -eq $ctrl -or $edit -eq [IntPtr]::Zero) { return $null }
    $dlg = $ctrl
    while ($null -ne $dlg -and $dlg.Current.ControlType -ne $CT::Window) { $dlg = $Walker.GetParent($dlg) }
    if ($null -eq $dlg) { return $null }
    return @{ Edit = $edit; Dialog = $dlg }
}
function Drive-FileDialog($path, $tries = 20) {
    for ($i = 0; $i -lt $tries; $i++) {
        Start-Sleep -Milliseconds 700
        $found = Get-FileNameEdit
        if ($null -eq $found) { continue }
        [Win32Dlg]::SendMessage($found.Edit, 0x000C, [IntPtr]::Zero, $path) | Out-Null      # WM_SETTEXT
        Start-Sleep -Milliseconds 300
        $sb = New-Object System.Text.StringBuilder 512
        [Win32Dlg]::SendMessage($found.Edit, 0x000D, [IntPtr]512, $sb) | Out-Null           # WM_GETTEXT
        if ($sb.ToString() -ne $path) { return "readback mismatch: '$($sb.ToString())'" }
        $ok = [Win32Dlg]::GetDlgItem([IntPtr]$found.Dialog.Current.NativeWindowHandle, 1)   # IDOK
        if ($ok -eq [IntPtr]::Zero) { return "IDOK not found" }
        [Win32Dlg]::SendMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null     # BM_CLICK
        Start-Sleep -Milliseconds 800
        return "driven (WM_SETTEXT + IDOK)"
    }
    return "file dialog never appeared"
}

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7
try {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "OpenSim Studio")
    $win = $null
    foreach ($try in 1..10) { $win = $AE::RootElement.FindFirst($TS::Children, $cond); if ($null -ne $win) { break }; Start-Sleep -Seconds 1 }
    if ($null -eq $win) { "FAIL: window not found"; exit 1 }

    # ---------- 1. Workspace + assembly import ----------
    "NAV:      $(Invoke-ById $win 'HomeWorkspaceFlow')"
    Start-Sleep -Seconds 2
    "IMPORT:   $(Invoke-ById $win 'ImportAssemblyButton')"
    "DIALOG:   $(Drive-FileDialog $Step)"
    $imported = Wait-Log $win "Assembly imported: \d+ bod" 90
    "IMPORTED: $imported"
    Check ($null -ne $imported) "the STEP file imported as an assembly"
    $bodyCount = if ($imported -match "Assembly imported: (\d+) bod") { [int]$Matches[1] } else { 0 }

    # ---------- 2. Material, mesh, moving-fluid environment, CFD analysis ----------
    "MATERIAL: $(Invoke-ById $win 'ApplyMaterialToAllButton')"
    "MESH:     $(Invoke-ById $win 'MeshAllBodiesButton')"
    $meshed = $null
    foreach ($i in 1..120) {
        Start-Sleep -Seconds 1
        $meshed = Get-TextMatching $win "^\d+ bod(y|ies) \(\d+ meshed\)$"
        if ($meshed -match "\((\d+) meshed\)" -and [int]$Matches[1] -eq $bodyCount) { break }
    }
    "MESHED:   $meshed"
    Check ($bodyCount -gt 0 -and $meshed -match "\((\d+) meshed\)" -and [int]$Matches[1] -eq $bodyCount) `
        "every body meshed"

    "MEDIUM:   $(Select-ComboItem $win 'MediumCombo' 'Moving fluid')"
    "SPEED:    $(Set-Value (Find-ById $win 'FlowSpeedBox') '0.2')"
    $radiation = Find-ById $win 'RadiationCheck'
    $radiation.SetFocus()          # commits the speed box (LostFocus binding)
    Start-Sleep -Milliseconds 250
    "RADIATE:  $(Set-Check $radiation $true)"

    "ANALYSIS: $(Select-ComboItem $win 'AnalysisCombo' 'computed airflow')"
    Check ((Get-ComboSelection $win 'AnalysisCombo') -like "*airflow*") "the CFD analysis is selected"

    # The Airflow panel appears only for the CFD analysis; its cell-size box also binds
    # LostFocus, so the write is committed by moving focus to the transient boxes.
    $cellBox = Wait-ById $win 'CfdCellSizeBox' 15
    Check ($null -ne $cellBox) "the Airflow (CFD) panel appeared for the CFD analysis"
    "CELL:     $(Set-Value $cellBox '0.001')"
    $t0Box = Find-ById $win 'InitialTemperatureBox'
    $t0Box.SetFocus(); Start-Sleep -Milliseconds 250
    $t0 = Set-Value $t0Box '350'
    $dur = Set-Value (Find-ById $win 'TransientDurationBox') '2'
    $dt = Set-Value (Find-ById $win 'TransientTimeStepBox') '0.2'
    "SETUP:    cell=0.001 m, T0=$t0 K, duration=$dur s, step=$dt s"

    # ---------- 3. Solve (voxelize → flow march → frozen-flow solid transient) ----------
    "SOLVE:    $(Invoke-ById $win 'SolveButton')"
    $merged = Wait-Log $win "Merged \d+ bodies" 120
    "MERGE:    $merged"
    Check ($null -ne $merged) "merge + contact detection ran"
    $done = Wait-Log $win "Conjugate solve complete" 900
    "DONE:     $done"
    Check ($null -ne $done) "the conjugate solve finished"
    if ($null -eq $done) { "ABORT: solve did not finish"; exit 1 }

    $voxLine = Find-LogAnywhere $win "Voxelized \d+"
    "VOXEL:    $voxLine"
    Check ($null -ne $voxLine) "the voxelization is stated in the log (cells, wall faces)"
    $reLine = Find-LogAnywhere $win "Re = "
    "REYNOLDS: $reLine"
    Check ($null -ne $reLine) "the Reynolds number is stated in the log"
    $frozenLine = Find-LogAnywhere $win "Frozen-flow transient"
    "FROZEN:   $frozenLine"
    Check ($null -ne $frozenLine) "the frozen-flow assumption is stated with its validity"

    # ---------- 4. The flow panel: summary + toggles ----------
    $summaryText = Get-Name (Find-ById $win 'FlowSummaryText')
    "FLOW:     $summaryText"
    Check ($summaryText -match "peak .* m/s") "the flow summary reports the resolved field"
    "ARROWS:   $(Set-Check (Find-ById $win 'FlowArrowsCheck') $true)"
    "STREAMS:  $(Set-Check (Find-ById $win 'FlowStreamlinesCheck') $true)"
    "SLICE:    $(Set-Check (Find-ById $win 'FlowSliceCheck') $true)"
    Start-Sleep -Milliseconds 800
    Check (-not $p.HasExited) "the viewport survived the flow overlays"

    # ---------- 5. The solid leg is a normal transient: the timeline is there ----------
    $slider = Wait-ById $win 'TimelineSlider' 60
    Check ($null -ne $slider) "the timeline appeared for the frozen-flow transient"

    # ---------- 6. Mechanical carries no CFD control ----------
    "NAV:      $(Invoke-ById $win 'WorkspaceStructuralButton')"
    Start-Sleep -Seconds 2
    $cfd2 = Find-ById $win 'CfdCellSizeBox'
    Check ($null -eq $cfd2 -or $cfd2.Current.IsOffscreen) "no CFD controls in the Mechanical workspace"

    Check (-not $p.HasExited) "the app is still alive"
}
catch {
    $failures.Add("the smoke script itself failed: $_")
    "SCRIPT ERROR: $_"
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}

if ($failures.Count -gt 0) {
    "";"SMOKE FAILED — $($failures.Count) check(s):"
    $failures | ForEach-Object { "  - $_" }
    exit 1
}
"";"SMOKE PASSED"
