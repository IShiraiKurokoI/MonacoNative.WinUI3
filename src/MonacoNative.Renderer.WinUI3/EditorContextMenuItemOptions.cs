using Microsoft.UI.Xaml;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Per-command context menu options, usable in XAML with standard dependency properties.
/// CommandId must match a registered Monaco editor command. Visibility controls whether
/// a menu item appears; IsEnabled is an additional guard independent of document state.
/// Mode controls both menu presentation and keyboard command dispatch. Legacy Visibility and
/// IsEnabled remain supported for menu-only customization when Mode is Default.
/// </summary>
public sealed class EditorContextMenuItemOptions : DependencyObject
{
    /// <summary>Default, ContextMenuAndShortcut, ShortcutOnly, or Disabled.</summary>
    public EditorCommandAvailability Mode
    {
        get => (EditorCommandAvailability)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(EditorCommandAvailability), typeof(EditorContextMenuItemOptions),
        new PropertyMetadata(EditorCommandAvailability.Default));

    public string CommandId
    {
        get => (string)GetValue(CommandIdProperty);
        set => SetValue(CommandIdProperty, value);
    }
    public static readonly DependencyProperty CommandIdProperty =
        DependencyProperty.Register(nameof(CommandId), typeof(string), typeof(EditorContextMenuItemOptions),
            new PropertyMetadata(string.Empty));

    public Visibility Visibility
    {
        get => (Visibility)GetValue(VisibilityProperty);
        set => SetValue(VisibilityProperty, value);
    }
    public static readonly DependencyProperty VisibilityProperty =
        DependencyProperty.Register(nameof(Visibility), typeof(Visibility), typeof(EditorContextMenuItemOptions),
            new PropertyMetadata(Visibility.Visible));

    public bool IsEnabled
    {
        get => (bool)GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.Register(nameof(IsEnabled), typeof(bool), typeof(EditorContextMenuItemOptions),
            new PropertyMetadata(true));
}
