// PORTED from src\HwSuite\Ui\LookDialogs.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace HwSuite.Ui;

/// <summary>Small themed prompts for saved lighting looks (name a look, confirm a delete). Nothing here touches hardware.</summary>
public sealed class LookDialog : DialogWindow
{
    readonly TextBox? _name;
    readonly TextBlock _error = B.Para("", "Muted", new Thickness(0, 6, 0, 0));
    readonly Func<string, string?>? _validate;
    public string Value { get; private set; } = "";

    LookDialog(string title, string message, string? initial, string action, Func<string, string?>? validate)
    {
        _validate = validate;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this.SetResourceReference(BackgroundProperty, "CanvasBrush"); this.SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 380 };
        root.Children.Add(B.Text(title, "CardTitle"));
        root.Children.Add(B.Para(message, "Muted", new Thickness(0, 6, 0, 12)));
        if (initial != null)
        {
            _name = new TextBox { Text = initial, MaxLength = Core.LightingLooks.MaxNameLength };
            _name.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Look name");
            _name.TextChanged += (_, _) => _error.Text = "";
            root.Children.Add(_name);
            B.Fg(_error, "WarnBrush");
            root.Children.Add(_error);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var ok = new Button { Content = action, Classes = { "PrimaryBtn" }, Margin = new Thickness(10, 0, 0, 0), IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        ok.Click += (_, _) =>
        {
            var v = _name?.Text.Trim() ?? "";
            if (_validate?.Invoke(v) is string problem) { _error.Text = problem; _name?.Focus(); return; }
            Value = v; DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        root.Children.Add(buttons);
        var frame = new Border { BorderThickness = new Thickness(1), Child = root };
        B.Bd(frame, "HairlineBrush"); B.Bg(frame, "CanvasBrush");
        Content = frame;
        Loaded += (_, _) => { if (_name != null) { _name.Focus(); _name.SelectAll(); } };
    }

    /// <summary>Ask for a look name; <paramref name="validate"/> returns a problem text or null. Null when cancelled.</summary>
    public static string? AskName(Window? owner, string title, string message, string initial, string action, Func<string, string?> validate)
    {
        var d = new LookDialog(title, message, initial, action, validate) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }

    public static bool Confirm(Window? owner, string title, string message, string action)
    {
        var d = new LookDialog(title, message, null, action, null) { Owner = owner };
        return d.ShowDialog() == true;
    }
}
