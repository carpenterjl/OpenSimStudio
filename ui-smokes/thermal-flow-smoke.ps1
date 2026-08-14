# UI test — the Thermal & Flow workspace end to end (Phase 6 Stage 1).
#
# Imports a multi-solid STEP file as an ASSEMBLY (one body per solid, placed by the
# file's own transforms), gives every body a material, meshes them all, sets the
# environment, runs the transient environment heat-flow solve, and then exercises the
# three result-display features this stage adds: the playback timeline (scrub / play /
# pause / tick labels), the editable colormap's absolute thresholds (legend bounds must
# follow), and per-body visibility. Finally it proves the Mechanical workspace is
# unchanged — no timeline there.
#
# Traps this script encodes (all found live):
#  - The Thermal & Flow analysis picker comes up BLANK; the analysis must be selected
#    explicitly before Solve does anything.
#  - A WPF Border/Rectangle has NO automation peer, so an AutomationId on one can never
#    be found: the legend is read through its TextBlocks (LegendMin/LegendMax) and the
#    timeline through its Slider, never through either panel's Border.
#  - LogService stamps every entry "[HH:mm:ss] ", so a log pattern anchored with ^ on the
#    message can only ever time out. Progress is read from the LOG, because panel
#    summaries read the same before and after a step.
#  - A ComboBox virtualizes its items: expand it before looking for a row.
#  - The Open dialog is driven purely by WINDOW HANDLE (WM_SETTEXT + BM_CLICK on IDOK).
#    SendKeys is retired — it types into whichever window holds focus.
param(
    [string]$Exe  = "C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio\OpenSim.App\bin\Debug\net8.0-windows\OpenSim.App.exe",
    # A real product-structure assembly (three placed instances of one part), generated
    # from the STEP assembly test fixtures. The repo's Example_Model.step is a SINGLE
    # solid, so it cannot exercise the multi-body path at all.
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
# NOTE: there is deliberately NO click-by-visible-text helper here. The home screen's tiles
# carry descriptive subtitles that name OTHER workspaces — "Import STEP assembly… / …
# (Thermal & Flow workspace)" — so a substring search for "Thermal" finds the IMPORT tile
# and opens a file browser instead of navigating (observed live). Every click is by id.
function Set-Value($e, $v) {
    if ($null -eq $e) { return "MISSING box" }
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v); return $v
}
function Get-Value($e) {
    if ($null -eq $e) { return $null }
    return $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Get-Name($e) { if ($null -eq $e) { return $null } return $e.Current.Name }
function Get-Range($e) { return $e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current }
function Set-Range($e, $v) { $e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($v) }
function Set-Check($e, [bool]$on) {
    if ($null -eq $e) { return "MISSING check" }
    $p = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $want = if ($on) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    $guard = 0
    while ($p.Current.ToggleState -ne $want -and $guard -lt 3) { $p.Toggle(); $guard++; Start-Sleep -Milliseconds 150 }
    return $p.Current.ToggleState
}
# A ComboBox virtualizes: expand it, THEN look for the row.
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
# The log is the only unambiguous progress signal: panel summaries read the same before
# and after a step (a pre-import "1 body (0 meshed)" matches the post-import pattern too),
# so waiting on THEM silently passes on stale state. The list auto-scrolls to the newest
# entry, so its realized items always include the tail.
# NOTE: every entry is stamped "[HH:mm:ss] " by LogService — patterns here must never be
# anchored with ^ on the message text, or the wait can only ever time out.
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
function Check($ok, $label) {
    if ($ok) { "PASS: $label" } else { $script:failures.Add($label); "FAIL: $label" }
}
# The file dialog's file-name EDIT, either dialog shape:
#   Open dialog: AutomationId 1148 = ComboBoxEx32 (NO UIA patterns) → ComboBox → Edit.
#   Save dialog: AutomationId 1001 = a plain Edit.
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
# Every top-level window owned by the app under test. One import click must produce exactly
# one extra window; the census makes a stale UIA element distinguishable from a real second
# dialog, which no amount of retry logic could tell apart.
function Get-AppWindows($processId) {   # NOT $pid — that is a read-only automatic variable
    $out = @()
    foreach ($w in $AE::RootElement.FindAll($TS::Children, (TypeCond ($CT::Window)))) {
        try { if ($w.Current.ProcessId -eq $processId) { $out += "'$($w.Current.Name)' [$($w.Current.ClassName)]" } }
        catch { }   # a window can die between the enumeration and the read
    }
    return $out
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
        # Deliberately NOT waiting for the element to disappear: a stale UIA element
        # outlives the window (measured — the window census reads 1 while Find-ById still
        # returns the dead file-name control), so that wait can only burn time and lie.
        # Whether the file actually opened is asserted from the app's own log downstream.
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
    # The app opens on the home screen, so the way in is the home TILE.
    "NAV:    $(Invoke-ById $win 'HomeThermalFlowTile')"
    Start-Sleep -Seconds 2
    $baseline = @(Get-AppWindows $p.Id)
    "WINDOWS BEFORE: $($baseline.Count) — $($baseline -join ' ; ')"
    "IMPORT: $(Invoke-ById $win 'ImportAssemblyButton')"
    # Sample repeatedly and keep the PEAK: the shell dialog takes a moment to appear, so a
    # single snapshot can miss it — and a second dialog stacked on the first would be
    # invisible to any check that only looks once.
    $peak = $baseline.Count; $peakList = $baseline
    foreach ($i in 1..12) {
        Start-Sleep -Milliseconds 500
        $now = @(Get-AppWindows $p.Id)
        if ($now.Count -gt $peak) { $peak = $now.Count; $peakList = $now }
    }
    "WINDOWS PEAK: $peak — $($peakList -join ' ; ')"
    Check ($peak -le $baseline.Count + 1) "one import click opened at most one file dialog"
    "DIALOG: $(Drive-FileDialog $Step)"
    $closed = @(Get-AppWindows $p.Id)
    "WINDOWS AFTER: $($closed.Count) — $($closed -join ' ; ')"
    Check ($closed.Count -eq $baseline.Count) "the dialog closed and left no second one behind"

    # The importer's own line, not the panel summary — see Get-LogMatching.
    $imported = Wait-Log $win "Assembly imported: \d+ bod" 90
    "IMPORTED: $imported"
    Check ($null -ne $imported) "the STEP file imported as an assembly"
    $bodyCount = if ($imported -match "Assembly imported: (\d+) bod") { [int]$Matches[1] } else { 0 }
    $summary = Get-TextMatching $win "^\d+ bod(y|ies) \(\d+ meshed\)$"
    "BODIES:   $summary"
    Check ($bodyCount -ge 2) "one body per solid ($bodyCount bodies)"
    Check ($summary -like "$bodyCount bod*") "the body list shows every imported part"

    # ---------- 2. Materials, mesh, environment, analysis ----------
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

    "MEDIUM:   $(Select-ComboItem $win 'MediumCombo' 'Still fluid')"
    "AMBIENT:  $(Set-Value (Find-ById $win 'AmbientBox') '293.15')"
    $radiation = Find-ById $win 'RadiationCheck'
    $radiation.SetFocus()          # commits the ambient box (LostFocus binding)
    Start-Sleep -Milliseconds 250
    "RADIATE:  $(Set-Check $radiation $true)"
    # The Thermal & Flow picker starts BLANK — the analysis must be chosen explicitly.
    "ANALYSIS: $(Select-ComboItem $win 'AnalysisCombo' 'Heat flow in an environment')"
    Check ((Get-ComboSelection $win 'AnalysisCombo') -like "*environment*") "environment analysis selected"

    # A short, coarse transient: 10 frames is plenty to scrub, and starting well above
    # ambient makes every frame visibly different.
    $t0 = Set-Value (Find-ById $win 'InitialTemperatureBox') '400'
    $dur = Set-Value (Find-ById $win 'TransientDurationBox') '2'
    $dt = Set-Value (Find-ById $win 'TransientTimeStepBox') '0.2'
    "SETUP:    T0=$t0 K, duration=$dur s, step=$dt s"

    # ---------- 3. Solve ----------
    "SOLVE:    $(Invoke-ById $win 'SolveButton')"
    $merged = Wait-Log $win "Merged \d+ bodies" 120
    "MERGE:    $merged"
    Check ($null -ne $merged) "merge + contact detection ran"
    $envLine = Get-LogMatching $win "Environment: "
    "ENV:      $envLine"
    Check ($null -ne $envLine) "the environment the solve used is stated in the log"

    # Probed by the SLIDER, not the panel Border — a Border has no automation peer.
    $slider = Wait-ById $win 'TimelineSlider' 180
    Check ($null -ne $slider) "the timeline appeared after an environment solve"
    if ($null -eq $slider) { "ABORT: no timeline"; exit 1 }

    # ---------- 4. Timeline: axis, scrub, play/pause, ticks ----------
    $r = Get-Range $slider
    "AXIS:     min=$($r.Minimum) max=$($r.Maximum) value=$($r.Value)"
    Check ($r.Maximum -gt $r.Minimum) "the timeline axis carries real frame VALUES (seconds), not indices"

    $frameBefore = Get-ComboSelection $win 'FrameCombo'
    # A value BETWEEN frames: the head must land on the nearest frame, not throw it away.
    $offGrid = $r.Minimum + 0.655 * ($r.Maximum - $r.Minimum)
    Set-Range $slider $offGrid
    Start-Sleep -Milliseconds 900
    $frameMid = Get-ComboSelection $win 'FrameCombo'
    "SCRUB:    to $([math]::Round($offGrid,4)) → time='$(Get-Name (Find-ById $win 'TimelineTimeText'))' frame='$frameMid'"
    Check ($frameMid -ne $frameBefore) "scrubbing off-grid moved the displayed frame (nearest-frame mapping)"

    Set-Range $slider $r.Minimum
    Start-Sleep -Milliseconds 900
    $frameStart = Get-ComboSelection $win 'FrameCombo'
    "START:    time='$(Get-Name (Find-ById $win 'TimelineTimeText'))' frame='$frameStart'"
    Check ($frameStart -ne $frameMid) "scrubbing back to the first frame followed"

    $play = Find-ById $win 'TimelinePlayButton'
    $labelIdle = Get-Name $play
    $play.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1500
    $playing = (Get-Range $slider).Value
    $labelPlaying = Get-Name (Find-ById $win 'TimelinePlayButton')
    "PLAY:     value=$([math]::Round($playing,4)) label '$labelIdle' → '$labelPlaying'"
    Check ($playing -gt $r.Minimum) "playback advanced the head on its own"
    Check ($labelPlaying -ne $labelIdle) "the play button became a pause button"

    (Find-ById $win 'TimelinePlayButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
    $paused = (Get-Range $slider).Value
    Start-Sleep -Milliseconds 1200
    $stillPaused = (Get-Range $slider).Value
    "PAUSE:    value=$([math]::Round($paused,4)) → $([math]::Round($stillPaused,4))"
    Check ($paused -eq $stillPaused) "pause froze the head"

    $ticks = Find-ById $win 'TimelineTicks'
    $tickLabels = @()
    if ($null -ne $ticks) {
        $tickLabels = @($ticks.FindAll($TS::Descendants, (TypeCond ($CT::Text))) | ForEach-Object { $_.Current.Name })
    }
    "TICKS:    $($tickLabels -join ' | ')"
    Check ($tickLabels.Count -ge 2) "the timeline carries axis tick labels"

    # ---------- 5. Colormap: preset + absolute thresholds drive the legend ----------
    $legendBefore = "$(Get-Name (Find-ById $win 'LegendMin')) … $(Get-Name (Find-ById $win 'LegendMax'))"
    "LEGEND:   auto  $legendBefore"
    "PRESET:   $(Select-ComboItem $win 'ColormapPresetCombo' 'Viridis')"
    Start-Sleep -Milliseconds 600
    "AUTO OFF: $(Set-Check (Find-ById $win 'ColormapAutoRangeCheck') $false)"
    # These boxes bind with the WPF default UpdateSourceTrigger (LostFocus) — right for a
    # threshold field, but it means a value set through UIA only reaches the view model
    # once focus LEAVES the box. Each write is followed by focus moving on.
    $minBox = Find-ById $win 'ColormapMinBox'
    $maxBox = Find-ById $win 'ColormapMaxBox'
    Set-Value $minBox '300' | Out-Null
    $maxBox.SetFocus(); Start-Sleep -Milliseconds 250
    Set-Value $maxBox '390' | Out-Null
    (Find-ById $win 'ColormapAutoRangeCheck').SetFocus(); Start-Sleep -Milliseconds 250
    "BOXES:    min='$(Get-Value $minBox)' max='$(Get-Value $maxBox)'"
    # The range path rebuilds texture coordinates through the 140 ms burst debounce.
    Start-Sleep -Seconds 2
    $legendAfter = "$(Get-Name (Find-ById $win 'LegendMin')) … $(Get-Name (Find-ById $win 'LegendMax'))"
    "LEGEND:   manual $legendAfter"
    Check ($legendAfter -ne $legendBefore) "absolute colormap thresholds moved the legend bounds"
    # Values outside the user range saturate — the legend says so with ▼ / ▲ markers.
    Check ($legendAfter -match "▲|▼") "the legend marks saturation at a user-set threshold"

    $legendTicks = Find-ById $win 'LegendTicks'
    $legendTickLabels = @()
    if ($null -ne $legendTicks) {
        $legendTickLabels = @($legendTicks.FindAll($TS::Descendants, (TypeCond ($CT::Text))) | ForEach-Object { $_.Current.Name })
    }
    "LEGEND TICKS: $($legendTickLabels -join ' | ')"
    Check ($legendTickLabels.Count -ge 2) "the legend carries value ticks at the colormap's stops"

    # ---------- 6. Per-body visibility ----------
    $list = Find-ById $win 'BodyList'
    $rows = @($list.FindAll($TS::Children, (TypeCond ($CT::ListItem))))
    $vis = $null
    if ($rows.Count -gt 0) { $vis = $rows[0].FindFirst($TS::Descendants, (TypeCond ($CT::CheckBox))) }
    "HIDE BODY 1: $(Set-Check $vis $false)"
    Start-Sleep -Milliseconds 800
    Check ($null -ne $vis) "each body row carries its own visibility toggle"
    "SHOW BODY 1: $(Set-Check $vis $true)"

    # ---------- 7. The other workspaces are untouched ----------
    "NAV:      $(Invoke-ById $win 'WorkspaceMechanicalButton')"
    Start-Sleep -Seconds 2
    $t2 = Find-ById $win 'TimelineSlider'
    Check ($null -eq $t2 -or $t2.Current.IsOffscreen) "no timeline in the Mechanical workspace"
    $env2 = Find-ById $win 'MediumCombo'
    Check ($null -eq $env2 -or $env2.Current.IsOffscreen) "no environment controls in the Mechanical workspace"

    Check (-not $p.HasExited) "the app is still alive"
}
catch {
    # A bug in THIS script must never read as a green smoke: without it, an aborted try
    # block falls straight through to the summary with an empty failure list.
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
