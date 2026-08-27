# UI test — the probe-fed COVERED patch (Stage C2): a coaxial probe through the substrate into
# a patch that is BURIED under a dielectric cover. It is the first mode where a vertical current
# and a buried source plane appear together, so the tube ends on an interior interface and the
# whole solve routes through the multi-layer (TLGF) kernels rather than the single-slab ones.
#
# Opens the RF & antenna workspace, picks "Probe-fed covered patch", sets the patch, the
# substrate, the cover and the coax, and expects a Zin line that names the multi-layer covered
# path — plus an assumptions line carrying the MULTI-LAYER probe list (an N-layer stackup and
# the interface-pinned tube), not the single-slab one. Then it checks the far field reports the
# surface-wave ledger, and that a bore too fat for the layer refuses by naming the LAYER.
#
# Drives the app purely through UI Automation (no test hooks). Traps encoded here, all paid for
# live in earlier batches: a WPF Border has no automation peer (read text through its
# TextBlocks), LostFocus-bound boxes need a focus nudge before the value reaches the viewmodel,
# clicking by visible text on the home screen fires the wrong tile (so navigation is by
# AutomationId only), and PowerShell -match is CASE-INSENSITIVE (so a refusal is matched by its
# phrase, never by two words that also occur in a panel blurb).
#
# A multi-layer probe solve is slow by construction — a stackup integrates a Sommerfeld contour
# per coupling-table knot where a slab evaluates a closed form — so the waits are generous.
# Self-bounded; prints a RESULT line.
param(
    [string]$Exe = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe"
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function TypeCond($type) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type) }
function Find-ById($root, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $c)
}
function Invoke-ById($root, $id) {
    $b = Find-ById $root $id; if ($null -eq $b) { return "MISSING $id" }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "clicked $id"
}
function Set-ValueById($root, $id, $v) {
    $e = Find-ById $root $id; if ($null -eq $e) { return "MISSING $id" }
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v)
    return "$id = $v"
}
function Nudge($root, $id) {
    $e = Find-ById $root $id
    if ($null -ne $e) { $e.SetFocus(); Start-Sleep -Milliseconds 200 }
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
function Get-TextMatching($root, $pattern) {
    foreach ($t in $root.FindAll($TS::Descendants, (TypeCond ($CT::Text)))) {
        if ($t.Current.Name -match $pattern) { return $t.Current.Name }
    }
    return $null
}
function Wait-Text($root, $pattern, $tries = 120) {
    foreach ($i in 1..$tries) { Start-Sleep -Seconds 1; $t = Get-TextMatching $root $pattern; if ($null -ne $t) { return $t } }
    return $null
}

$fail = 0
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7
try {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "OpenSim Studio")
    $win = $null
    foreach ($try in 1..10) { $win = $AE::RootElement.FindFirst($TS::Children, $cond); if ($null -ne $win) { break }; Start-Sleep -Seconds 1 }
    if ($null -eq $win) { "FAIL: window not found"; exit 1 }

    $nav = Invoke-ById $win 'HomeWorkspaceRf'
    if ($nav -like "MISSING*") { $nav = Invoke-ById $win 'WorkspaceRfButton' }
    "NAV: $nav"
    Start-Sleep -Seconds 3
    if ($null -eq (Find-ById $win 'AntennaSourceCombo')) {
        Invoke-ById $win 'StudyStepSetup' | Out-Null
        Start-Sleep -Seconds 2
    }
    if ($null -eq (Find-ById $win 'AntennaSourceCombo')) { "FAIL: antenna panel not shown"; exit 1 }

    "MODE: $(Select-ComboItem $win 'AntennaSourceCombo' 'Probe-fed covered patch')"

    # A Balanis-scale patch: 12 x 9 mm on 1.588 mm of eps_r 2.2, 0.8 mm of the same material as
    # cover, with the probe well inside the footprint: the attachment fan needs an INTERIOR
    # mesh vertex, and a coarse mesh snaps a quarter-inset probe onto the rim (measured).
    # The mesh ceiling is lambda/10 at the HIGHEST swept frequency, so FMax pins the
    # element size; with ONE sweep point the solve runs at FMin.
    "SET: $(Set-ValueById $win 'PlateWidthBox' '12')"
    "SET: $(Set-ValueById $win 'PlateLengthBox' '9')"
    "SET: $(Set-ValueById $win 'PatchHeightBox' '1.588')"
    "SET: $(Set-ValueById $win 'SubstrateEpsRBox' '2.2')"
    "SET: $(Set-ValueById $win 'CoverThicknessBox' '0.8')"
    "SET: $(Set-ValueById $win 'ProbeXBox' '0')"
    "SET: $(Set-ValueById $win 'ProbeYBox' '-1.5')"
    "SET: $(Set-ValueById $win 'ProbeRadiusBox' '0.15')"
    "SET: $(Set-ValueById $win 'ProbeSegmentsBox' '3')"
    "SET: $(Set-ValueById $win 'SweepFMinBox' '9400')"
    # The far field runs at the single-frequency box, not at the sweep, so it is pinned too.
    "SET: $(Set-ValueById $win 'FrequencyBox' '9400')"
    "SET: $(Set-ValueById $win 'SweepFMaxBox' '20000')"
    "SET: $(Set-ValueById $win 'SweepPointsBox' '1')"
    Nudge $win 'SweepPointsBox'          # commits the last LostFocus-bound box

    "SOLVE: $(Invoke-ById $win 'SolveAntennaButton')"
    $zin = Wait-Text $win "Zin = |Not solvable" 240
    "RESULT: $zin"
    if ($null -eq $zin -or $zin -notmatch "Zin = ") { "FAIL: the probe-fed covered patch produced no Zin"; $fail++ }
    elseif ($zin -notmatch "covered patch") { "FAIL: the result does not name the covered multi-layer path"; $fail++ }

    # The assumptions must be the MULTI-LAYER probe list, not the single-slab one: a buried
    # source over an N-layer stackup, with every internal interface forced to be a tube node.
    $assume = Get-TextMatching $win "Assumptions: "
    "ASSUMPTIONS: $assume"
    if ($null -eq $assume) { "FAIL: no assumptions line"; $fail++ }
    elseif ($assume -notmatch "N-layer grounded stackup") { "FAIL: the single-slab probe assumptions were shown for a covered patch"; $fail++ }
    elseif ($assume -notmatch "tube node") { "FAIL: the interface-node rule is not stated"; $fail++ }

    # The far field must carry the coherent surface-wave ledger (the probe's tube and junction
    # legs join both sides of it).
    "FARFIELD: $(Invoke-ById $win 'FarFieldButton')"
    $ff = Wait-Text $win "Far field at|Not computable" 240
    "FARFIELD RESULT: $ff"
    if ($null -eq $ff -or $ff -notmatch "Far field at") { "FAIL: no far field"; $fail++ }
    elseif ($ff -notmatch "surface wave P_sw") { "FAIL: the far field does not report the surface-wave ledger"; $fail++ }

    # A bore too fat for the layer is a typed refusal that names the LAYER, not just the stack:
    # 3 elements of 0.529 mm cannot carry a 0.3 mm bore (the reduced-kernel element >= 2*radius
    # floor). Matched by the phrase, since -match is case-insensitive.
    "SET: $(Set-ValueById $win 'ProbeRadiusBox' '0.35')"
    Nudge $win 'SweepPointsBox'
    "SOLVE2: $(Invoke-ById $win 'SolveAntennaButton')"
    $refusal = Wait-Text $win "too thin for the probe bore" 120
    "REFUSAL: $refusal"
    if ($null -eq $refusal) { "FAIL: a bore too fat for the layer was not refused by name"; $fail++ }
    elseif ($refusal -notmatch "Layer 0") { "FAIL: the refusal does not name which layer"; $fail++ }

    if ($fail -eq 0) { "PASSED: the probe-fed covered patch solves through the multi-layer kernels, radiates, and refuses by name" }
    else { "FAILED: $fail check(s)" }
}
catch {
    "FAIL: $($_.Exception.Message)"
    $fail++
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
if ($fail -gt 0) { exit 1 }
