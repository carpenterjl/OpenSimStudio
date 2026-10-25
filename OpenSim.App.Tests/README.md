# OpenSim.App.Tests — view-model tests

xunit tests of the WPF application's view models, run headless (no `Application`, no window,
no dispatcher pump). Added under FU-48 of `plans/Electrical-Follow-Up-Plan-2026-10.md`.

## Why a second test project

`OpenSim.Tests` targets `net8.0` and cannot reference the `net8.0-windows` app (NU1201).
Retargeting it would put the app in the dependency chain of every focused solver-test run,
and the app recompiles whenever any library DLL changes — its WPF markup pass keys on the DLL
timestamps, not on the reference assemblies the C# compiler uses to skip unchanged
dependents. Measured on the development machine (Debug, 2026-10-19):

| Build | Time |
|---|---|
| Full recompile, project alone: OpenSim.Pcb / OpenSim.App / OpenSim.Tests | 5.4 s / 13.2 s / 9.5 s |
| Nothing changed: OpenSim.Tests / OpenSim.App | 1.6 s / 1.5 s |
| One Pcb source touched, no API change: OpenSim.Tests / OpenSim.App | 2.5 s / 7.3 s |

So the app is built only when these tests run:

```bash
dotnet test OpenSim.App.Tests/OpenSim.App.Tests.csproj
```

## How the view models are composed

`Support/AppComposition.cs` builds the container with the application's own registrations
(`OpenSim.App/Services/CompositionRoot.cs`, which `App.OnStartup` also uses) and replaces three
things: the log (the app's marshals onto `Application.Current.Dispatcher`), and the material
and colormap libraries and the recent-projects list (rooted at a private temporary directory
instead of the user profile). `MainViewModel` itself resolves headless, so the project
save/open plumbing behind the dialogs is testable (`CaptureProjectSettings`,
`LoadProjectFromPath`).
Brush-bearing view models construct on xunit's MTA worker threads; frozen Freezables need no
dispatcher. `PcbViewModel.RefreshCopperPreview` recomposes synchronously when there is no
`Application`, so the copper preview tree can be asserted directly.

## What cannot be tested here

Code paths that open a file dialog (`Microsoft.Win32.*FileDialog`, 30 sites in 17 view-model
files), touch `Application.Current.MainWindow` or `TryFindResource`, or wait on a
`DispatcherTimer` / `Dispatcher.BeginInvoke`. An async command whose continuation touches
Freezables (`await Task.Run(...)` then a `PointCollection`) CAN be run: `Support/DispatcherPump.cs`
installs a WPF dispatcher on the test thread and pumps it until the command's task completes,
as `AntennaViewModelPrintedTests` does for the antenna solve. Tests target the plumbing behind
the rest, through
`internal` seams (`InternalsVisibleTo("OpenSim.App.Tests")` in `OpenSim.App.csproj` and
`OpenSim.Core.csproj`), e.g. `PcbViewModel.InstallBoard`, `BuildNetMeshOptions`,
`BuildStackupSettings`, `LoadStackupSettings`; `MainViewModel.CaptureProjectSettings`, `LoadProjectFromPath`.
