using System.ComponentModel;
using System.Windows;
using AvalonDock.Layout.Serialization;
using OpenSim.App.Services;
using OpenSim.App.ViewModels;

namespace OpenSim.App;

/// <summary>
/// Code-behind limited to visual behaviour: log auto-scroll, progress-bar visibility,
/// and per-workspace dock-layout persistence. Viewport interaction lives in
/// <see cref="Views.Viewport3DView"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += (_, _) => RestoreLayout();
        Closing += (_, _) => SaveLayout();
        viewModel.Log.Entries.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    LogList.ScrollIntoView(LogList.Items[^1]);
                }), System.Windows.Threading.DispatcherPriority.Background);
        };
        viewModel.Session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectSession.IsBusy))
            SolveProgress.Visibility = _viewModel.Session.IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Dock layout persistence ----------------

    /// <summary>One layout shared by both workspaces: the four panes are identical —
    /// only their CONTENT switches (via visibility), so re-deserializing on a workspace
    /// switch would only re-host the same panes (AvalonDock transiently rebinds pane
    /// content to the LayoutAnchorable while doing so — measured, noisy, pointless).
    /// Versioned filename: pane re-keying later just bumps to v3 instead of trying to
    /// reconcile stale XML (v1 was the pre-workspace-split layout).</summary>
    private static readonly string LayoutFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenSimStudio", "layout.v2.xml");

    private void RestoreLayout()
    {
        try
        {
            if (!System.IO.File.Exists(LayoutFile)) return;
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, e) =>
            {
                // A pane saved by another app version has no matching ContentId here.
                if (e.Content is null) e.Cancel = true;
            };
            serializer.Deserialize(LayoutFile);
        }
        catch (Exception)
        {
            _viewModel.Log.Append("Saved window layout could not be restored; using the default layout.");
        }
    }

    private void SaveLayout()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LayoutFile)!);
            new XmlLayoutSerializer(DockManager).Serialize(LayoutFile);
        }
        catch
        {
            // Never block shutdown on a layout-save failure.
        }
    }
}
