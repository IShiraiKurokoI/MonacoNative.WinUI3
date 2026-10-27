using Microsoft.UI.Xaml;
namespace MonacoNative.NuGetExample;
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Editor.Text = "// WinUI 3 NuGet example\r\nint value = 42;\r\n";
        Closed += (_, _) => Editor.Dispose();
    }
}
