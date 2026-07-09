using System.ComponentModel;
using System.Windows.Controls;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views.Panels;

/// <summary>Code-behind: scroll the net picked in the viewport into view.</summary>
public partial class PcbNetsPanel : UserControl
{
    private MainViewModel? _viewModel;

    public PcbNetsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_viewModel is not null || DataContext is not MainViewModel viewModel) return;
        _viewModel = viewModel;
        viewModel.Pcb.PropertyChanged += OnPcbPropertyChanged;
    }

    private void OnPcbPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PcbViewModel.SelectedNetRow)
            && _viewModel!.Pcb.SelectedNetRow is not null)
            NetList.ScrollIntoView(_viewModel.Pcb.SelectedNetRow);
    }
}
