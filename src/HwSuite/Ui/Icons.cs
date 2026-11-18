using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace HwSuite.Ui;

/// <summary>Line icons on a 24-unit grid, drawn with a 1.5 px stroke at any size (geometry is scaled, the stroke is not).</summary>
static class Icons
{
    static readonly Dictionary<string, string> Data = new()
    {
        ["dashboard"] = "M4,4 H10 V11 H4 Z M14,4 H20 V8 H14 Z M14,12 H20 V20 H14 Z M4,15 H10 V20 H4 Z",
        ["cooling"] = "M12,10.2 A1.8,1.8 0 1 0 12,13.8 A1.8,1.8 0 1 0 12,10.2 Z M12,10.2 C11.4,6.2 12.6,3.2 15.4,3.6 C17.8,4 17.6,7.6 13.6,11 M13.7,12.9 C17.4,14.4 19.3,17 17.6,19 C16,20.9 13.1,18.8 12.2,13.8 M10.4,12.9 C7.2,15.2 4,15.4 3.4,12.9 C2.9,10.6 6.2,9.4 10.3,11.2",
        ["lighting"] = "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 Z M12,2.5 V5 M12,19 V21.5 M2.5,12 H5 M19,12 H21.5 M5.3,5.3 L7.1,7.1 M16.9,16.9 L18.7,18.7 M18.7,5.3 L16.9,7.1 M5.3,18.7 L7.1,16.9",
        ["system"] = "M4,7 H20 M4,12 H20 M4,17 H20 M9,5 V9 M15,10 V14 M8,15 V19",
        ["motherboard"] = "M4,4 H20 V20 H4 Z M8,8 H13 V13 H8 Z M16.5,7 V11 M16.5,14 V17 M7.5,16.5 H12.5 M4,9 H2 M4,15 H2 M22,9 H20 M22,15 H20",
        ["dimm"] = "M2,8 H22 V15 H2 Z M5,10.5 H8 V13 H5 Z M10.5,10.5 H13.5 V13 H10.5 Z M16,10.5 H19 V13 H16 Z M4,15 V17 M7,15 V17 M17,15 V17 M20,15 V17",
        ["gpu"] = "M2,6 H22 V17 H2 Z M8.5,9 A2.5,2.5 0 1 0 8.5,14 A2.5,2.5 0 1 0 8.5,9 Z M15.5,9 A2.5,2.5 0 1 0 15.5,14 A2.5,2.5 0 1 0 15.5,9 Z M4,17 V20 M9,17 V19 H15 V17",
        ["aio"] = "M3,3 H10 V21 H3 Z M3,7 H10 M3,11 H10 M3,15 H10 M17.5,13 A3,3 0 1 0 17.5,19 A3,3 0 1 0 17.5,13 Z M10,6 C14.5,6 16.5,9 17.5,13 M10,18 H14.5",
        ["strip"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7.5 A4.5,4.5 0 1 0 12,16.5 A4.5,4.5 0 1 0 12,7.5 Z M12,3 V7.5 M12,16.5 V21 M3,12 H7.5 M16.5,12 H21",
        ["mouse"] = "M12,3 C8.4,3 6.5,5.8 6.5,9.5 V14.5 C6.5,18.3 8.9,21 12,21 C15.1,21 17.5,18.3 17.5,14.5 V9.5 C17.5,5.8 15.6,3 12,3 Z M12,6.5 V9.5 M6.5,11 H17.5",
        ["keyboard"] = "M2,6 H22 V18 H2 Z M5.5,9.5 H6.5 M9,9.5 H10 M12.5,9.5 H13.5 M16,9.5 H18.5 M5.5,12.5 H6.5 M9,12.5 H10 M12.5,12.5 H13.5 M16,12.5 H18.5 M7,15.5 H17",
        ["moon"] = "M20,14.5 A8.5,8.5 0 1 1 9.5,4 A6.8,6.8 0 0 0 20,14.5 Z",
        ["warn"] = "M12,3.5 L21.5,20 H2.5 Z M12,9.5 V14 M12,16.8 V17.2",
        ["chip"] = "M7,7 H17 V17 H7 Z M10,3 V7 M14,3 V7 M10,17 V21 M14,17 V21 M3,10 H7 M3,14 H7 M17,10 H21 M17,14 H21",
        ["power"] = "M12,3 V11 M7.5,6.2 A7.5,7.5 0 1 0 16.5,6.2",
        ["palette"] = "M12,3 C7,3 3,6.8 3,11.5 C3,16.2 6.8,20 11.2,20 C12.4,20 13,19.2 13,18.3 C13,17.2 12.2,16.6 12.2,15.7 C12.2,14.6 13.1,13.8 14.2,13.8 H16.4 C19,13.8 21,11.9 21,9.5 C21,5.9 17,3 12,3 Z M7.5,11 A1,1 0 1 0 7.5,11.01 M10,7.2 A1,1 0 1 0 10,7.21 M14.5,7.2 A1,1 0 1 0 14.5,7.21",
        ["info"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,10.5 V16.5 M12,7.3 V7.7",
    };

    /// <summary>Icon whose stroke follows a theme resource (e.g. "TextSecondaryBrush").</summary>
    public static Path Icon(string name, double size, string brushKey)
    {
        var p = Icon(name, size);
        p.SetResourceReference(Shape.StrokeProperty, brushKey);
        return p;
    }

    public static Path Icon(string name, double size = 16, Brush? stroke = null)
    {
        var g = Geometry.Parse(Data[name]).Clone();
        g.Transform = new ScaleTransform(size / 24.0, size / 24.0);
        var p = new Path
        {
            Data = g, Width = size, Height = size, Stretch = Stretch.None, StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = false, VerticalAlignment = VerticalAlignment.Center
        };
        if (stroke != null) p.Stroke = stroke;
        return p;
    }

    /// <summary>Icon whose stroke follows the Foreground of the nearest ancestor control (nav items, buttons).</summary>
    public static Path Bound(string name, double size, Type ancestor)
    {
        var p = Icon(name, size);
        p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, ancestor, 1) });
        return p;
    }

    public static StackPanel WithText(string icon, string text, double size, Type ancestor)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var p = Bound(icon, size, ancestor);
        p.Margin = new Thickness(0, 0, 10, 0);
        sp.Children.Add(p);
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, ancestor, 1) });
        sp.Children.Add(t);
        return sp;
    }
}
