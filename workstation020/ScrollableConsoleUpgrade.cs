using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool ScrollConsoleHook = RegisterScrollConsoleHook();
    private bool _scrollConsoleReady;

    private static bool RegisterScrollConsoleHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_ScrollConsoleLoaded),
            true);
        return true;
    }

    private static void MainWindow_ScrollConsoleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeScrollableConsole();
    }

    private void InitializeScrollableConsole()
    {
        if (_scrollConsoleReady) return;
        _scrollConsoleReady = true;

        CommandTranscriptBox.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        CommandTranscriptBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        CommandTranscriptBox.TextWrapping = TextWrapping.Wrap;
        CommandTranscriptBox.Focusable = true;
        CommandTranscriptBox.IsTabStop = true;

        CommandTranscriptBox.PreviewMouseWheel += CommandTranscript_PreviewMouseWheel;
        CommandTranscriptBox.PreviewKeyDown += CommandTranscript_PreviewKeyDown;

        AppendDiagnostic("Command console scroll controller active: wheel, Page Up/Down, Home/End.");
    }

    private void CommandTranscript_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = FindConsoleScrollViewer(CommandTranscriptBox);
        if (viewer is null) return;

        var steps = Math.Max(1, Math.Abs(e.Delta) / 40);
        for (var i = 0; i < steps; i++)
        {
            if (e.Delta > 0) viewer.LineUp();
            else viewer.LineDown();
        }

        e.Handled = true;
    }

    private void CommandTranscript_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var viewer = FindConsoleScrollViewer(CommandTranscriptBox);
        if (viewer is null) return;

        switch (e.Key)
        {
            case Key.PageUp:
                viewer.PageUp();
                e.Handled = true;
                break;
            case Key.PageDown:
                viewer.PageDown();
                e.Handled = true;
                break;
            case Key.Home when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                viewer.ScrollToTop();
                e.Handled = true;
                break;
            case Key.End when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                viewer.ScrollToEnd();
                e.Handled = true;
                break;
        }
    }

    private static ScrollViewer? FindConsoleScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindConsoleScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
