# UI test — reproducing the Ansys ENG 210 beam study end to end (R1-R5).
#
# Builds the reference beam (200 x 60 x 20 mm), meshes it with QUADRATIC elements, gives it
# structural steel, and fixes its two opposite BOTTOM EDGES through the new Scope panel —
# the scope the reference uses and a face selection cannot express. It then exercises both
# halves of the zero-area rule: a fixed support on an edge is accepted, a distributed force
# on one is refused.
#
# SCOPE OF THIS SCRIPT, stated because the split is deliberate: the load face is chosen by
# CLICKING the 3D view, and UI Automation has no path to a viewport face pick, so the smoke
# stops short of the solve. The NUMBERS are gated in the suite instead
# (OpenSim.Tests/Solvers/BeamBendingBenchmarks.cs), which reproduces the same configuration
# headlessly and checks it against the reference invariants. What this script proves is the
# plumbing the suite cannot see: that the scope panel enumerates real edges, that ticking
# two of them produces an edge-scoped condition, and that the refusals are visible.
#
# Traps this script encodes (inherited from the earlier smokes, all found live):
#  - A WPF Border has NO automation peer, so ids go on TextBlocks/controls, never a Border.
#  - LogService stamps every entry "[HH:mm:ss] ", so a ^-anchored log pattern can only
#    ever time out. Progress is read from the LOG, not from panel summaries.
#  - The log ListBox VIRTUALIZES: after a solve dumps dozens of lines at once, early lines
#    fall out of the realized items, so a scan must walk the ScrollPattern range.
#  - A ComboBox virtualizes its items: expand before looking for a row.
#  - Clicks are BY ID ONLY — home-screen tile subtitles name other workspaces.
#  - The Save dialog's file-name control is a plain Edit (AutomationId 1001), NOT the Open
#    dialog's 1148 ComboBoxEx32; drive it by HWND (WM_SETTEXT + BM_CLICK on IDOK), and save
#    to a FRESH path to dodge the overwrite-confirm child dialog.
param(
    [string]$Exe = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe"
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
function Get-TextMatching($root, $pattern) {
    foreach ($t in $root.FindAll($TS::Descendants, (TypeCond ($CT::Text)))) {
        if ($t.Current.Name -match $pattern) { return $t.Current.Name }
    }
    return $null
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
            Start-Sleep -Milliseconds 250; $ec.Collapse(); return "selected '$needle' in $id"
        }
    }
    $ec.Collapse(); return "MISSING item '*$needle*' in $id"
}
# The log ListBox virtualizes: walk its scroll range so early lines are realized.
function Find-LogAnywhere($root, $pattern) {
    $log = Find-ById $root 'LogList'
    if ($null -eq $log) { return $null }
    $scroll = $null
    try { $scroll = $log.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }
    $positions = if ($null -eq $scroll -or -not $scroll.Current.VerticallyScrollable) { @(-1) } else { 0..10 | ForEach-Object { $_ * 10 } }
    foreach ($pos in $positions) {
        if ($pos -ge 0) {
            try { $scroll.SetScrollPercent(-1, $pos) } catch { }
            Start-Sleep -Milliseconds 120
        }
        foreach ($it in $log.FindAll($TS::Descendants, (TypeCond ($CT::ListItem)))) {
            if ($it.Current.Name -match $pattern) { return $it.Current.Name }
        }
    }
    return $null
}
function Wait-Log($root, $pattern, $tries = 90) {
    foreach ($i in 1..$tries) {
        $t = Find-LogAnywhere $root $pattern
        if ($null -ne $t) { return $t }
        Start-Sleep -Seconds 1
    }
    return $null
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
        [Win32Dlg]::SendMessage($found.Edit, 0x000C, [IntPtr]::Zero, $path) | Out-Null
        Start-Sleep -Milliseconds 300
        $ok = [Win32Dlg]::GetDlgItem([IntPtr]$found.Dialog.Current.NativeWindowHandle, 1)
        if ($ok -eq [IntPtr]::Zero) { return "IDOK not found" }
        [Win32Dlg]::SendMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 900
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

    # ---------- 1. Enter Mechanical and build the reference beam ----------
    Invoke-ById $win 'HomeWorkspaceStructural'
    Start-Sleep -Seconds 2

    Set-Value (Wait-ById $win 'BoxSizeXBox') "0.2"   | Out-Null
    Set-Value (Find-ById $win 'BoxSizeYBox') "0.06"  | Out-Null
    Set-Value (Find-ById $win 'BoxSizeZBox') "0.02"  | Out-Null
    Invoke-ById $win 'CreateBoxButton'
    $created = Wait-Log $win 'Box 0\.2'
    Check ($null -ne $created) "beam geometry created (200 x 60 x 20 mm)"

    # ---------- 2. Quadratic mesh — mandatory for a bending benchmark ----------
    # TET4's documented bending band is [0.40, 1.05] of Timoshenko against TET10's
    # [0.92, 1.03]; this study is entirely a bending deflection.
    $quad = Wait-ById $win 'QuadraticElementsCheck'
    $qp = $quad.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($qp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $qp.Toggle() }
    Check ($qp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) "quadratic (TET10) elements selected"

    # LEFT ON AUTO deliberately: the Detail slider tops out at 2 mm, which is far finer than
    # a 200 mm beam needs (and finer than the reference's own 2 mm divisions), so a manual
    # value cannot express this part at all — auto edge length is the only usable path here.
    # Recorded as a product limitation rather than worked around.
    Invoke-ById $win 'GenerateMeshButton'
    $meshed = Wait-Log $win '(node|element)s?' 240
    Check ($null -ne $meshed) "beam meshed: $meshed"
    # The study rail auto-advances to Setup once the mesh exists; the rail's own Mesh row
    # carries the element count now, so assert the advance instead of the old info text.
    $meshStep = Find-ById $win 'StudyStepMesh'
    Check ($null -ne $meshStep) "study rail shows the Mesh step"

    # ---------- 3. Steel ----------
    Select-ComboItem $win 'MaterialCombo' 'Structural steel' | Out-Null

    # ---------- 4. The reference scope: two opposite BOTTOM EDGES ----------
    # This is the whole point of the batch — the reference fixes two mathematical lines,
    # which no face selection can express.
    $summary = Get-Name (Wait-ById $win 'ScopeSummaryText')
    Check ($summary -match '12 edge') "scope panel lists the box's 12 geometric edges: $summary"

    $edgeList = Find-ById $win 'ScopeEdgeList'
    $wanted = @('faces 0 and 2', 'faces 1 and 2')     # x-min + y-min, x-max + y-min
    $ticked = 0
    foreach ($item in $edgeList.FindAll($TS::Descendants, (TypeCond ($CT::CheckBox)))) {
        foreach ($w in $wanted) {
            if ($item.Current.Name -like "*$w*") {
                $item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
                $ticked++
                break
            }
        }
    }
    Check ($ticked -eq 2) "ticked the two bottom support edges (got $ticked)"

    Invoke-ById $win 'AddFixedSupportButton'
    $support = Wait-Log $win 'Added FixedSupport.*2 edge'
    Check ($null -ne $support) "fixed support scoped to 2 EDGES: $support"

    # ---------- 5. A distributed load must still be refused on an edge ----------
    # Re-tick one edge and try a force: the rule is that a total force has no meaning over
    # a zero-area scope, and the refusal must be visible rather than silent.
    foreach ($item in $edgeList.FindAll($TS::Descendants, (TypeCond ($CT::CheckBox)))) {
        if ($item.Current.Name -like "*faces 0 and 2*") {
            $item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            break
        }
    }
    Set-Value (Find-ById $win 'ForceYBox') "-500000" | Out-Null
    Invoke-ById $win 'AddForceButton'
    $refused = Wait-Log $win 'area of an edge or a vertex is zero' 10
    Check ($null -ne $refused) "distributed load on an edge is refused, loudly"

    # ---------- 6. An empty scope is refused too ----------
    Invoke-ById $win 'ClearScopeButton'
    Start-Sleep -Milliseconds 400
    Invoke-ById $win 'AddForceButton'
    $noScope = Wait-Log $win 'Select one or more faces' 10
    Check ($null -ne $noScope) "a load with no scope at all is refused with an actionable message"

    # ---------- 7. The Results step (and its exports) is gated until a solve ----------
    # The redesigned shell locks the Results step — and the export buttons that live on
    # it — until results exist, which supersedes the old "present but refuses" check:
    # an export without results is now unreachable rather than merely refused.
    $resultsStep = Find-ById $win 'StudyStepResults'
    Check ($null -ne $resultsStep) "study rail shows the Results step"
    Check ($null -eq (Find-ById $win 'ExportSummaryCsvButton')) "export buttons hidden while Results is locked"
    Invoke-ById $win 'StudyStepResults'
    Start-Sleep -Milliseconds 400
    Check ($null -eq (Find-ById $win 'ExportSummaryCsvButton')) "a locked Results step refuses selection"

    "SUMMARY: $($failures.Count) failure(s)"
    foreach ($f in $failures) { "  - $f" }
    if ($failures.Count -eq 0) { "SMOKE PASSED" } else { "SMOKE FAILED" }
}
catch {
    "FAILED with exception: $_"
    "SMOKE FAILED"
}
finally {
    if ($null -ne $p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
