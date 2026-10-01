using Microsoft.UI.Xaml;

namespace MonacoNative.SampleApp;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        // The sample has a feature sidebar and an in-editor Find/Replace panel;
        // open at a usable desktop size instead of the small WinUI default.
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        _window.Activate();
    }
}
