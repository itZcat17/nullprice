using System.Windows;

namespace Nullprice.Sheaf.App;

/// <summary>
/// Scaffold shell. Deliberately plain code-behind rather than MVVM — this exists to drive
/// the engine and prove it works end to end, and is expected to be replaced once the
/// interaction design is settled.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
