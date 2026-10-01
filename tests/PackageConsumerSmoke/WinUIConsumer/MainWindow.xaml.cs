using Microsoft.UI.Xaml;
namespace MonacoNative.PackageSmoke.WinUI;
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Editor.Text = "// NuGet XAML consumer smoke\r\nint value = 42;\r\n";
        Closed += (_, _) => Editor.Dispose();
    }
}
