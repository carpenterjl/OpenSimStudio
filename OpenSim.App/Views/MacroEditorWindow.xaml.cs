using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenSim.App.ViewModels;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.App.Views;

/// <summary>The ZPL macro window (OP-99). View glue only: the file dialogs, and INPUT and PAUSE,
/// which the macro asks for from its background thread and this window shows on the UI thread.</summary>
public partial class MacroEditorWindow : Window, IZplHost
{
    private readonly OpticsMacroEditorViewModel _macros;

    public MacroEditorWindow(OpticsMacroEditorViewModel macros)
    {
        InitializeComponent();
        _macros = macros;
        DataContext = macros;
        macros.Host = this;
        Closed += (_, _) => { if (ReferenceEquals(macros.Host, this)) macros.Host = null; };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open ZPL macro",
            Filter = "ZPL macros (*.zpl)|*.zpl|All files (*.*)|*.*",
            InitialDirectory = _macros.MacroFolder,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) _macros.Open(dialog.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save ZPL macro",
            Filter = "ZPL macros (*.zpl)|*.zpl",
            InitialDirectory = _macros.MacroFolder,
            FileName = System.IO.Path.GetFileName(_macros.FileName),
        };
        if (dialog.ShowDialog(this) == true) _macros.Save(dialog.FileName);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Macro folder", InitialDirectory = _macros.MacroFolder };
        if (dialog.ShowDialog(this) == true) _macros.MacroFolder = dialog.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    string? IZplHost.Input(string prompt) => Dispatcher.Invoke(() => Prompt(prompt));

    void IZplHost.Pause(string message) =>
        Dispatcher.Invoke(() => MessageBox.Show(this, message.Length > 0 ? message : "Paused.", "ZPL macro", MessageBoxButton.OK));

    /// <summary>A small dialog with the prompt, a text box, OK and Cancel; null when cancelled.</summary>
    private string? Prompt(string prompt)
    {
        var box = new TextBox { MinWidth = 260, Margin = new Thickness(0, 6, 0, 6) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 4, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 70 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "ZPL macro input", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        dialog.SetResourceReference(BackgroundProperty, "PanelBgBrush");
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Text : null;
    }
}
