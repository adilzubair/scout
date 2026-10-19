using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Scout.App;

internal static class SettingsScroll
{
    // One detent moves one short text row; precision-wheel deltas stay proportional.
    internal static double Target(double offset, int delta, double maximum) => Math.Clamp(offset - delta / 120d * 32, 0, maximum);
    internal static void Attach(ScrollViewer viewer)
    {
        viewer.CanContentScroll = false; viewer.PanningMode = PanningMode.VerticalOnly;
        viewer.PreviewMouseWheel += (_, e) =>
        {
            // A multiline field gets its own scrolling until it reaches an edge.
            // Fractional DPI layout can leave offsets a few ulps below the maximum.
            for (var node = e.OriginalSource as DependencyObject; node != null && node != viewer; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is ScrollViewer nested && nested.ScrollableHeight > 0
                    && (e.Delta > 0 && nested.VerticalOffset > .5 || e.Delta < 0 && nested.VerticalOffset < nested.ScrollableHeight - .5))
                { nested.ScrollToVerticalOffset(Target(nested.VerticalOffset, e.Delta, nested.ScrollableHeight)); e.Handled = true; return; }
            viewer.ScrollToVerticalOffset(Target(viewer.VerticalOffset, e.Delta, viewer.ScrollableHeight)); e.Handled = true;
        };
    }
}
