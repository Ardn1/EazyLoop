using System.Windows;

namespace EazyLoop;

public abstract class ThemedCanvas : FrameworkElement
{
    private static readonly DependencyProperty ThemeRevisionProperty = DependencyProperty.Register(
        "ThemeRevision", typeof(int), typeof(ThemedCanvas), new FrameworkPropertyMetadata(0,
            (element, _) => ((ThemedCanvas)element).ThemeChanged()));

    protected ThemedCanvas() => SetResourceReference(ThemeRevisionProperty, "ThemeRevision");

    protected virtual void ThemeChanged() => InvalidateVisual();
}
