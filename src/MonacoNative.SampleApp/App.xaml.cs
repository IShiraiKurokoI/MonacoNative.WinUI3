using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

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
        // Position the sample (not the reusable editor) in the working area of
        // the closest monitor. Clamping also keeps it usable on small screens.
        var display = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var width = Math.Min(1280, work.Width);
        var height = Math.Min(820, work.Height);
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width, height));
        _window.Activate();
    }
}
