# UI test - the copper heat exchanger (internal-flow conjugate CFD).
#
# Reproduces, through the shipping UI, the setup of a 2023 Ansys Fluent coursework study:
# a 300 x 200 x 50 mm copper block with a serpentine water channel, water in at 363.15 K,
# environment 300 K, transient. The workflow it proves:
#   import the STEP ASSEMBLY (Copper Body + Water)
#     -> mark "Water" as a FLUID VOLUME (it must never be meshed as metal)
#     -> material + mesh the copper
#     -> still-air environment at 300 K
#     -> CFD domain "Internal", working fluid Water, DETECT OPENINGS from the fluid body
#     -> the bore mouths come back as two ports; inlet speed + inlet temperature
#     -> solve -> outlet mixing-cup temperature in the log, temperature slice in the view.
#
# Inherits every UIA trap of the earlier smokes (no click-by-text, Border has no peer,
# LogService "[HH:mm:ss] " prefix defeats ^-anchored patterns, LostFocus boxes need a
# focus nudge, hwnd-driven file dialog, the log ListBox VIRTUALIZES).
#
# The cell size is set EXPLICITLY and coarse (5 mm) and the march is short: this is a
# WORKFLOW smoke, not the reproduction. The published numbers come from the Release
# benchmark, the way the 128-squared cavity does.
param(
    [string]$Exe  = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe",
    [string]$Step = "C:\Users\Carpe\Desktop\ENG Project 2 Heat Exchanger\Heat Exchanger v1.step"
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
    $imported = Wait-Log $win "Assembly imported: \d+ bod" 120
    "IMPORTED: $imported"
    Check ($imported -match "Assembly imported: 2 bod") "the STEP assembly imported as two bodies"

    # ---------- 2. The Water body is a FLUID VOLUME, not metal ----------
    # The rail auto-advances to Mesh on import, and BodiesPanel rides on both steps.
    $list = Wait-ById $win 'BodyList' 30
    Check ($null -ne $list) "the body list is there"
    $waterRow = $null
    foreach ($it in $list.FindAll($TS::Descendants, (TypeCond ($CT::ListItem)))) {
        foreach ($t in $it.FindAll($TS::Descendants, (TypeCond ($CT::Text)))) {
            if ($t.Current.Name -like "Water*") { $waterRow = $it; break }
        }
        if ($null -ne $waterRow) { break }
    }
    Check ($null -ne $waterRow) "the Water body is listed"
    if ($null -ne $waterRow) {
        $waterRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 400
        "FLUIDROLE: $(Set-Check (Find-ById $win 'BodyIsFluidCheck') $true)"
    }

    # ---------- 3. Copper on the solid, mesh it ----------
    "MATERIAL: $(Select-ComboItem $win 'MaterialCombo' 'Copper')"
    "APPLY:    $(Invoke-ById $win 'ApplyMaterialToAllButton')"
    "MESH:     $(Invoke-ById $win 'MeshAllBodiesButton')"
    $skipped = Wait-Log $win "is a fluid volume, not material" 180
    "SKIPPED:  $skipped"
    Check ($null -ne $skipped) "the fluid volume was NOT meshed, and said so"
    $meshedLog = Wait-Log $win "nodes, [\d,]+ elements" 600
    "MESHED:   $meshedLog"
    Check ($null -ne $meshedLog) "the copper body meshed"

    # ---------- 4. Still air at 300 K around the block ----------
    "STEP:     $(Invoke-ById $win 'StudyStepEnvironment')"
    Start-Sleep -Seconds 1
    "MEDIUM:   $(Select-ComboItem $win 'MediumCombo' 'Still fluid')"
    "FLUID:    $(Select-ComboItem $win 'FluidCombo' 'Air')"
    $ambient = Find-ById $win 'AmbientBox'
    "AMBIENT:  $(Set-Value $ambient '300')"
    $radiation = Find-ById $win 'RadiationCheck'
    $radiation.SetFocus(); Start-Sleep -Milliseconds 250     # commits the LostFocus box
    "RADIATE:  $(Set-Check $radiation $true)"

    # ---------- 5. The CFD case: internal domain, water, detected openings ----------
    "ANALYSIS: $(Select-ComboItem $win 'AnalysisCombo' 'computed airflow')"
    $domainCombo = Wait-ById $win 'CfdDomainModeCombo' 20
    Check ($null -ne $domainCombo) "the CFD setup panel appeared for the conjugate analysis"
    "DOMAIN:   $(Select-ComboItem $win 'CfdDomainModeCombo' 'Internal')"
    Start-Sleep -Milliseconds 500
    "CFDFLUID: $(Select-ComboItem $win 'CfdFluidCombo' 'Water')"
    "CELL:     $(Set-Value (Find-ById $win 'CfdCellSizeBox') '0.005')"
    (Find-ById $win 'CfdWallMarginBox').SetFocus(); Start-Sleep -Milliseconds 250

    "DETECT:   $(Invoke-ById $win 'CfdDetectOpeningsButton')"
    Start-Sleep -Seconds 2
    $status = Get-Name (Find-ById $win 'CfdOpeningsStatus')
    "OPENINGS: $status"
    Check ($status -match "2 openings found") "both bore mouths were detected from the fluid body"
    Check ($status -match "XMin" -and $status -match "XMax") "the mouths are on the two end faces"

    # Port 1 is the inlet (seeded); give it the reference stream.
    "SPEED:    $(Set-Value (Find-ById $win 'OpeningSpeed1') '0.1')"
    $tempBox = Find-ById $win 'OpeningTemp1'
    Check ($null -ne $tempBox) "the inlet carries its own stream temperature, separate from the ambient"
    if ($null -ne $tempBox) {
        $tempBox.SetFocus(); Start-Sleep -Milliseconds 200
        "INLET T:  $(Set-Value $tempBox '363.15')"
    }

    # ---------- 6. Transient settings + solve ----------
    "STEP:     $(Invoke-ById $win 'StudyStepSetup')"
    Start-Sleep -Seconds 1
    $t0 = Find-ById $win 'InitialTemperatureBox'
    $t0.SetFocus(); Start-Sleep -Milliseconds 250
    "T0:       $(Set-Value $t0 '300')"
    "DURATION: $(Set-Value (Find-ById $win 'TransientDurationBox') '2')"
    "STEPSIZE: $(Set-Value (Find-ById $win 'TransientTimeStepBox') '1')"

    "STEP:     $(Invoke-ById $win 'StudyStepSolve')"
    Start-Sleep -Seconds 1
    "SOLVE:    $(Invoke-ById $win 'SolveButton')"
    $done = Wait-Log $win "Conjugate solve complete" 2400
    "DONE:     $done"
    Check ($null -ne $done) "the conjugate solve finished"
    if ($null -eq $done) { "ABORT: solve did not finish"; exit 1 }

    # ---------- 7. What the reference report quotes ----------
    $reLine = Find-LogAnywhere $win "Re = "
    "REYNOLDS: $reLine"
    Check ($reLine -match "L = 0\.0[0-9]") "the Reynolds number measures the PASSAGE, not the block"
    $portIn = Find-LogAnywhere $win "Opening 1 \(XMin\): in "
    "INLET:    $portIn"
    Check ($portIn -match "mixing-cup T = 363") "the inlet stream arrives at its own temperature"
    $portOut = Find-LogAnywhere $win "Opening 2 \(XMax\): out "
    "OUTLET:   $portOut"
    Check ($null -ne $portOut) "the outlet reports a mixing-cup temperature"
    $trace = Find-LogAnywhere $win "Outlet temperature history"
    Check ($null -ne $trace) "the outlet temperature is reported as a time series"
    $surroundings = Find-LogAnywhere $win "Surroundings: still Air"
    "SURROUND: $surroundings"
    Check ($null -ne $surroundings) "the unwetted skin is carried by the air correlations"

    # ---------- 8. The temperature contour ----------
    "STEP:     $(Invoke-ById $win 'StudyStepResults')"
    Start-Sleep -Seconds 1
    "SLICE:    $(Set-Check (Find-ById $win 'FlowSliceCheck') $true)"
    Start-Sleep -Milliseconds 600
    "QUANTITY: $(Select-ComboItem $win 'SliceQuantityCombo' 'Temperature')"
    Start-Sleep -Seconds 1
    $legend = Get-Name (Find-ById $win 'SliceLegendText')
    "LEGEND:   $legend"
    Check ($legend -match "Temperature: .* K") "the slice paints temperature in kelvin"
    "FIXED:    $(Set-Check (Find-ById $win 'SliceFixedRangeCheck') $true)"
    (Find-ById $win 'SliceMinBox').SetFocus(); Start-Sleep -Milliseconds 200
    "MIN:      $(Set-Value (Find-ById $win 'SliceMinBox') '300')"
    (Find-ById $win 'SliceMaxBox').SetFocus(); Start-Sleep -Milliseconds 200
    "MAX:      $(Set-Value (Find-ById $win 'SliceMaxBox') '363.15')"
    (Find-ById $win 'SliceMinBox').SetFocus(); Start-Sleep -Milliseconds 400
    $legend2 = Get-Name (Find-ById $win 'SliceLegendText')
    "PINNED:   $legend2"
    Check ($legend2 -match "300 . 363") "the colour range can be pinned to the reference figure"

    Check (-not $p.HasExited) "the app survived the whole workflow"
}
catch {
    $failures.Add("the smoke script itself failed: $_")
    "SCRIPT ERROR: $_"
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}

if ($failures.Count -gt 0) {
    "";"SMOKE FAILED - $($failures.Count) check(s):"
    $failures | ForEach-Object { "  - $_" }
    exit 1
}
"";"SMOKE PASSED"
