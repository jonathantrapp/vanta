using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HwSuite.Ui;

/// <summary>
/// Theme selector: one card per theme with a small preview drawn in that theme's own colours (not the current theme), its
/// name and a one-line description. Selecting applies live (<see cref="MainWindow.ApplyTheme"/>) and is remembered.
/// Keyboard: Tab to the group, arrows/Space select (radio-button semantics).
/// </summary>
public sealed class ThemePicker : UserControl
{
    readonly List<(RadioButton rb, ThemePalette theme)> _items = new();
    public event Action<ThemePalette>? Picked;

    public ThemePicker()
    {
        var panel = new WrapPanel();
        foreach (var t in Themes.All)
        {
            var rb = new RadioButton { GroupName = "theme", Tag = t, Margin = new Thickness(0, 0, 12, 12), Cursor = System.Windows.Input.Cursors.Hand, Template = CardTemplate(), FocusVisualStyle = B.S("FocusRing") };
            rb.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, t.Name + " theme");
            rb.Content = Preview(t);
            rb.Checked += (_, _) => { if (!_syncing) Picked?.Invoke(t); };
            _items.Add((rb, t));
            panel.Children.Add(rb);
        }
        Content = panel;
        Sync();
        Themes.Changed += Sync;
        Unloaded += (_, _) => Themes.Changed -= Sync;
        Loaded += (_, _) => { Themes.Changed -= Sync; Themes.Changed += Sync; Sync(); };
    }

    bool _syncing;
    void Sync()
    {
        _syncing = true;
        foreach (var (rb, t) in _items) rb.IsChecked = t.Id == Themes.Current.Id;
        _syncing = false;
    }

    static ControlTemplate CardTemplate()
    {
        // selection ring in the CURRENT theme's accent; the preview inside keeps its own colours
        var t = new ControlTemplate(typeof(RadioButton));
        var border = new FrameworkElementFactory(typeof(Border), "B");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(cp);
        t.VisualTree = border;
        var check = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        check.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "B"));
        t.Triggers.Add(check);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverTintBrush"), "B"));
        t.Triggers.Add(hover);
        return t;
    }

    static SolidColorBrush Br(ThemePalette t, string token) { var b = new SolidColorBrush(t[token]); b.Freeze(); return b; }

    /// <summary>A miniature window in the theme's own colours.</summary>
    static FrameworkElement Preview(ThemePalette t)
    {
        var root = new StackPanel { Width = 220 };
        var canvas = new Border { Height = 112, CornerRadius = new CornerRadius(7), Background = Br(t, "Canvas"), BorderBrush = Br(t, "Hairline"), BorderThickness = new Thickness(1), ClipToBounds = true };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var side = new Border { Background = Br(t, "Sidebar"), BorderBrush = Br(t, "Hairline"), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideStack = new StackPanel { Margin = new Thickness(7, 10, 7, 0) };
        sideStack.Children.Add(new Border { Height = 7, Width = 20, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = Br(t, "Accent"), Margin = new Thickness(0, 0, 0, 10) });
        for (int i = 0; i < 3; i++) sideStack.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2), Background = Br(t, i == 0 ? "Text" : "TextTertiary"), Margin = new Thickness(0, 0, 6, 7) });
        side.Child = sideStack;
        g.Children.Add(side);
        var main = new StackPanel { Margin = new Thickness(9, 10, 9, 0) };
        main.Children.Add(new Border { Height = 7, Width = 70, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = Br(t, "Text"), Margin = new Thickness(0, 0, 0, 9) });
        var panel = new Border { Height = 58, CornerRadius = new CornerRadius(5), Background = Br(t, "Panel"), BorderBrush = Br(t, "Hairline"), BorderThickness = new Thickness(1), Padding = new Thickness(7) };
        var ps = new Grid();
        var line = new Polyline { Stroke = Br(t, "Series1"), StrokeThickness = 1.6, Points = new PointCollection { new(0, 26), new(14, 20), new(28, 23), new(42, 12), new(56, 15), new(70, 8), new(84, 12), new(98, 6) } };
        var line2 = new Polyline { Stroke = Br(t, "Series2"), StrokeThickness = 1.6, Points = new PointCollection { new(0, 32), new(14, 30), new(28, 31), new(42, 27), new(56, 28), new(70, 24), new(84, 26), new(98, 23) } };
        ps.Children.Add(line); ps.Children.Add(line2);
        var btn = new Border { Width = 38, Height = 14, CornerRadius = new CornerRadius(3), Background = Br(t, "Accent"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        ps.Children.Add(btn);
        var txt = new Border { Width = 46, Height = 5, CornerRadius = new CornerRadius(2), Background = Br(t, "TextSecondary"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
        ps.Children.Add(txt);
        panel.Child = ps;
        main.Children.Add(panel);
        Grid.SetColumn(main, 1);
        g.Children.Add(main);
        canvas.Child = g;
        root.Children.Add(canvas);
        var name = B.Text(t.Name, "CardTitle", 13.5, null, new Thickness(4, 10, 0, 0));
        root.Children.Add(name);
        var blurb = B.Para(t.Blurb, "Muted", new Thickness(4, 2, 4, 4));
        root.Children.Add(blurb);
        return root;
    }
}
