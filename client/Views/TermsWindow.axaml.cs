using Avalonia.Controls;

namespace client.Views;

/// <summary>
/// Standalone Terms &amp; Conditions modal — deliberately styled independently of the
/// tracker shell (neutral palette, plain title, no shell brushes/branding). Shown as a
/// blocking modal over the main window while pending terms exist; closing is refused
/// until every term is accepted (see code-behind + App.axaml.cs wiring).
/// </summary>
public partial class TermsWindow : Window
{
    public TermsWindow()
    {
        InitializeComponent();
    }
}
