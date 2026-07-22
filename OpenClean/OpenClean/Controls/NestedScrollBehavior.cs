using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenClean.Controls;

/// <summary>
/// Attached Property, die den typischen WPF-Scroll-Konflikt zwischen einer inneren,
/// höhenbegrenzten Liste (eigener ScrollViewer) und dem umgebenden Seiten-ScrollViewer löst.
///
/// Standardmäßig schluckt der innere ScrollViewer jedes Mausrad-Event, sobald der Zeiger
/// über ihm steht – die Seite scrollt dann selbst dann nicht weiter, wenn die innere Liste
/// bereits am oberen bzw. unteren Anschlag angekommen ist.
///
/// Mit <c>BubbleWheel="True"</c> wird das Event nur so lange von der inneren Liste verarbeitet,
/// wie sie in der gewünschten Richtung noch scrollen kann. Danach wird es an das Eltern-Element
/// weitergereicht, sodass der äußere ScrollViewer übernimmt.
///
/// Verwendung in XAML:
/// <code>
/// &lt;ListBox ctl:NestedScrollBehavior.BubbleWheel="True" MaxHeight="240" /&gt;
/// </code>
/// </summary>
public static class NestedScrollBehavior
{
    /// <summary>
    /// Ist der Wert <c>true</c>, wird das Mausrad-Event am Anschlag des inneren
    /// ScrollViewers an das Eltern-Element weitergereicht.
    /// </summary>
    public static readonly DependencyProperty BubbleWheelProperty =
        DependencyProperty.RegisterAttached(
            "BubbleWheel",
            typeof(bool),
            typeof(NestedScrollBehavior),
            new PropertyMetadata(false, OnBubbleWheelChanged));

    /// <summary>Liest den Wert der Attached Property <c>BubbleWheel</c>.</summary>
    public static bool GetBubbleWheel(DependencyObject element)
    {
        if (element is null)
        {
            return false;
        }

        return (bool)element.GetValue(BubbleWheelProperty);
    }

    /// <summary>Setzt den Wert der Attached Property <c>BubbleWheel</c>.</summary>
    public static void SetBubbleWheel(DependencyObject element, bool value)
    {
        element?.SetValue(BubbleWheelProperty, value);
    }

    /// <summary>Hängt den Handler je nach neuem Wert an bzw. wieder ab.</summary>
    private static void OnBubbleWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        // Erst immer abhängen, damit ein wiederholtes Setzen keine Doppel-Registrierung erzeugt.
        element.PreviewMouseWheel -= OnPreviewMouseWheel;

        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    /// <summary>
    /// Prüft, ob der innere ScrollViewer in die gedrehte Richtung noch Platz hat.
    /// Falls nicht (oder falls gar keiner gefunden wird), wird das Event als behandelt
    /// markiert und ein neues Mausrad-Event am Eltern-Element ausgelöst.
    /// </summary>
    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            if (e is null || e.Handled)
            {
                return;
            }

            if (sender is not UIElement element)
            {
                return;
            }

            // Delta 0 (z. B. horizontales Rad) sicherheitshalber ignorieren.
            if (e.Delta == 0)
            {
                return;
            }

            ScrollViewer? inner = FindScrollViewer(element);
            if (inner is not null && CanScrollFurther(inner, e.Delta))
            {
                // Innere Liste kann noch scrollen -> Event normal durchreichen.
                return;
            }

            // Am Anschlag oder kein eigener ScrollViewer: nach oben weiterreichen.
            e.Handled = true;
            BubbleToParent(element, e);
        }
        catch
        {
            // Ein Scroll-Helfer darf die Anwendung unter keinen Umständen abbrechen.
        }
    }

    /// <summary>
    /// Löst eine Kopie des Mausrad-Events am Eltern-Element aus, damit der
    /// äußere ScrollViewer die Bewegung übernimmt.
    /// </summary>
    private static void BubbleToParent(UIElement element, MouseWheelEventArgs e)
    {
        if (VisualTreeHelper.GetParent(element) is not UIElement parent)
        {
            return;
        }

        MouseWheelEventArgs forwarded = new(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = element
        };

        parent.RaiseEvent(forwarded);
    }

    /// <summary>
    /// Gibt <c>true</c> zurück, wenn der ScrollViewer in der durch <paramref name="delta"/>
    /// vorgegebenen Richtung noch scrollbaren Inhalt besitzt. Positives Delta = hoch.
    /// </summary>
    private static bool CanScrollFurther(ScrollViewer scrollViewer, int delta)
    {
        // Gar kein überstehender Inhalt -> nichts zu scrollen.
        if (scrollViewer.ExtentHeight <= scrollViewer.ViewportHeight)
        {
            return false;
        }

        const double tolerance = 0.5;

        if (delta > 0)
        {
            // Hochscrollen: möglich, solange der Offset nicht bei 0 steht.
            return scrollViewer.VerticalOffset > tolerance;
        }

        // Runterscrollen: möglich, solange der Offset nicht am Maximum steht.
        return scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - tolerance;
    }

    /// <summary>
    /// Sucht den ersten ScrollViewer im Visual Tree unterhalb von <paramref name="root"/>
    /// (bei einer ListBox steckt er im Control-Template). Gibt <c>null</c> zurück,
    /// wenn keiner vorhanden ist.
    /// </summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject? child = VisualTreeHelper.GetChild(root, i);
            if (child is null)
            {
                continue;
            }

            ScrollViewer? found = FindScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
