using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow
{
    private WindowState _restoreWindowState = WindowState.Normal;
    private WindowState? _hiddenWindowState;

    private void ConfigureWindowChrome()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Let NSWindow own its rounded frame and traffic lights. Cropping the
            // client tree cannot provide native window geometry or safe captions.
            SystemDecorations = SystemDecorations.Full;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome
                | ExtendClientAreaChromeHints.OSXThickTitleBar;
            ExtendClientAreaTitleBarHeightHint = 60;
            RootWindowBorder.CornerRadius = default;
            RootWindowBorder.ClipToBounds = false;
            RootWindowBorder.BorderThickness = default;
            RootWindowBorder.BoxShadow = default;
            // These are parts of the window surface, not individually rounded
            // cards. A single backdrop must reach every native window corner.
            DragBar.CornerRadius = default;
            this.FindControl<Border>("SidebarShell")!.CornerRadius = default;
            var statusBar = this.FindControl<Border>("WindowStatusBar")!;
            statusBar.CornerRadius = default;
            statusBar.Background = Brushes.Transparent;
            DragBar.Padding = new Thickness(88, 0, 20, 0);
            QuitAppBtn.IsVisible = HideToTrayBtn.IsVisible = MaximizeBtn.IsVisible = false;
        }
        MaximizeBtn.Click += (_, _) => ToggleMaximize();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            if (WindowState != WindowState.Minimized) _restoreWindowState = WindowState;
            var maximized = WindowState == WindowState.Maximized;
            RootWindowBorder.CornerRadius = new CornerRadius(OperatingSystem.IsMacOS() || maximized ? 0 : 16);
            MaximizeGlyph.Data = Geometry.Parse(maximized
                ? "M 5,3 L 13,3 L 13,11 M 3,5 L 11,5 L 11,13 L 3,13 Z"
                : "M 3,3 L 13,3 L 13,13 L 3,13 Z");
        };
        // NSWindow handles native edge resizing; do not intercept its caption area.
        if (OperatingSystem.IsMacOS()) return;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || WindowState != WindowState.Normal) return;
            var edge = ResizeEdgeAt(e.GetPosition(this), Bounds.Size);
            if (edge is null) return;
            e.Handled = true;
            BeginResizeDrag(edge.Value, e);
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            var edge = WindowState == WindowState.Normal ? ResizeEdgeAt(e.GetPosition(this), Bounds.Size) : null;
            Cursor = edge switch
            {
                WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
                WindowEdge.East or WindowEdge.West => new Cursor(StandardCursorType.SizeWestEast),
                WindowEdge.NorthWest or WindowEdge.SouthEast => new Cursor(StandardCursorType.TopLeftCorner),
                WindowEdge.NorthEast or WindowEdge.SouthWest => new Cursor(StandardCursorType.TopRightCorner),
                _ => Cursor.Default
            };
        }, RoutingStrategies.Tunnel);
        PointerExited += (_, _) => Cursor = Cursor.Default;
    }

    internal static WindowEdge? ResizeEdgeAt(Point point, Size size)
    {
        const double grip = 7;
        if (point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height) return null;
        var left = point.X < grip;
        var right = point.X >= size.Width - grip;
        var top = point.Y < grip;
        var bottom = point.Y >= size.Height - grip;
        if (left && top) return WindowEdge.NorthWest;
        if (right && top) return WindowEdge.NorthEast;
        if (left && bottom) return WindowEdge.SouthWest;
        if (right && bottom) return WindowEdge.SouthEast;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        return null;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual visual && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
        e.Handled = true;
    }
}
