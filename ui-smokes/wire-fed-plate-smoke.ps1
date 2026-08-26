# UI test — the wire-fed plate (Stage D1/D3): the first mode in which a thin WIRE and an
# RWG SHEET appear in one structure.
#
# Opens the RF & antenna workspace, picks "Wire-fed plate" in the geometry combo, sets the
# plate, the attached wire and a single-point sweep, and expects a Zin line reporting BOTH
# unknown counts ("N RWG + M wire unknowns") plus the incidence angle the solver measured
# from the built geometry. Then it checks the two honest refusals the mode carries: the
# near field and the field overlay are typed messages naming why, and turning the ground
# plane on refuses by naming free space as the scope.
#
# Drives the app purely through UI Automation (no test hooks). Traps encoded here, all paid
# for live in earlier batches: a WPF Border has no automation peer (read text through its
# TextBlocks), LostFocus-bound boxes need a focus nudge before the value reaches the
# viewmodel, and clicking by visible text on the home screen fires the wrong tile — so
# navigation is by AutomationId only. Self-bounded; prints a RESULT line.
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
function Set-Check($root, $id, [bool]$on) {
    $e = Find-ById $root $id; if ($null -eq $e) { return "MISSING $id" }
    $p = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $want = if ($on) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    $guard = 0
    while ($p.Current.ToggleState -ne $want -and $guard -lt 3) { $p.Toggle(); $guard++; Start-Sleep -Milliseconds 150 }
    return "$id = $($p.Current.ToggleState)"
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
function Wait-Text($root, $pattern, $tries = 60) {
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

    # Navigation by AutomationId only: the home tiles carry other workspaces' names in their
    # subtitles, so a substring click on visible text fires the wrong thing (observed live).
    # The app opens on the HOME screen, where the nav rail does not exist yet — so the home
    # tile is what gets clicked, and the rail button is only the fallback for a session that
    # is already inside a workspace.
    $nav = Invoke-ById $win 'HomeWorkspaceRf'
    if ($nav -like "MISSING*") { $nav = Invoke-ById $win 'WorkspaceRfButton' }
    "NAV: $nav"
    Start-Sleep -Seconds 3
    if ($null -eq (Find-ById $win 'AntennaSourceCombo')) {
        Invoke-ById $win 'StudyStepSetup' | Out-Null
        Start-Sleep -Seconds 2
    }
    if ($null -eq (Find-ById $win 'AntennaSourceCombo')) { "FAIL: antenna panel not shown"; exit 1 }

    "MODE: $(Select-ComboItem $win 'AntennaSourceCombo' 'Wire-fed plate')"

    # A single sweep point at 300 MHz keeps the RWG fill small; the mesh ceiling is
    # lambda/10 at the HIGHEST swept frequency, so pinning fmax pins the element size too.
    "SET: $(Set-ValueById $win 'PlateWidthBox' '300')"
    "SET: $(Set-ValueById $win 'PlateLengthBox' '300')"
    "SET: $(Set-ValueById $win 'SweepFMaxBox' '600')"
    "SET: $(Set-ValueById $win 'SweepPointsBox' '1')"
    "SET: $(Set-ValueById $win 'AttachedWireLengthBox' '60')"
    "SET: $(Set-ValueById $win 'AttachedWireAngleBox' '90')"
    "SET: $(Set-ValueById $win 'AttachXBox' '0')"
    "SET: $(Set-ValueById $win 'AttachYBox' '0')"
    Nudge $win 'SweepPointsBox'          # commits the last LostFocus-bound box

    "SOLVE: $(Invoke-ById $win 'SolveAntennaButton')"
    # Wait for the FINISHED line: the transient "Solving (N RWG + M wire unknowns)..." status
    # also contains "wire unknowns", so a loose pattern reads the placeholder as the answer.
    $zin = Wait-Text $win "Zin = |Not solvable" 90
    "RESULT: $zin"
    if ($null -eq $zin -or $zin -notmatch "wire unknowns") { "FAIL: the hybrid solve produced no Zin"; $fail++ }
    elseif ($zin -notmatch "incidence") { "FAIL: the result does not report the measured incidence"; $fail++ }

    # The far field is available and reports the free-space power ledger, which is the
    # identity that caught the junction disc sign at -5.9.
    "FARFIELD: $(Invoke-ById $win 'FarFieldButton')"
    $ff = Wait-Text $win "Far field at|Not computable" 90
    "FARFIELD RESULT: $ff"
    if ($null -eq $ff -or $ff -notmatch "Far field at") { "FAIL: no far field"; $fail++ }
    elseif ($ff -notmatch "of the power the feed delivers") { "FAIL: the far field does not report the power ledger"; $fail++ }

    # The two honest refusals: no hybrid near-field transform yet, so both the near field
    # and the overlay say so by name instead of quietly painting the sheet alone.
    "NEARFIELD: $(Invoke-ById $win 'NearFieldButton')"
    $nf = Wait-Text $win "named\s+follow-up" 20
    "NEARFIELD RESULT: $nf"
    if ($null -eq $nf) { "FAIL: the near field did not refuse by name"; $fail++ }

    # A ground plane is outside the mode's scope, and the refusal must name free space.
    "GROUND: $(Set-Check $win 'GroundPlaneCheck' $true)"
    "SOLVE2: $(Invoke-ById $win 'SolveAntennaButton')"
    # PowerShell -match is CASE-INSENSITIVE, and the panel blurb says "in free space or over
    # an infinite PEC ground" - so match the refusal phrase, not the two words.
    $refusal = Wait-Text $win "solved in FREE SPACE" 30
    "REFUSAL: $refusal"
    if ($null -eq $refusal) { "FAIL: a grounded wire-fed plate was not refused by name"; $fail++ }

    if ($fail -eq 0) { "PASSED: wire-fed plate solves, radiates, and refuses what it cannot do" }
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
