using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Optimization Wizard dialog (OP-56). View glue only: it closes when OK has
/// applied the rows.</summary>
public partial class MeritWizardWindow : Window
{
    public MeritWizardWindow(OpticsMeritWizardViewModel wizard)
    {
        InitializeComponent();
        DataContext = wizard;
        void OnCloseRequested(object? sender, EventArgs e) => Close();
        wizard.CloseRequested += OnCloseRequested;
        Closed += (_, _) => wizard.CloseRequested -= OnCloseRequested;
    }
}
