// OP-88
using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The non-sequential Optimization and Bitmap Wizards dialog (OP-88). View glue only:
/// it closes when OK has applied the rows.</summary>
public partial class NscMeritWizardWindow : Window
{
    public NscMeritWizardWindow(OpticsNscMeritWizardViewModel wizard)
    {
        InitializeComponent();
        DataContext = wizard;
        void OnCloseRequested(object? sender, EventArgs e) => Close();
        wizard.CloseRequested += OnCloseRequested;
        Closed += (_, _) => wizard.CloseRequested -= OnCloseRequested;
    }
}
