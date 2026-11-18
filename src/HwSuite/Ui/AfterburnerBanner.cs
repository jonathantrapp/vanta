using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HwSuite.Hardware;

namespace HwSuite.Ui;

/// <summary>
/// Shown when MSI Afterburner is running and either its software fan curve is active (read from its own config) while HwSuite
/// fan control is on, or the engine has just caught another program overwriting a GPU fan. Offers the safe helper actions.
/// </summary>
public sealed class AfterburnerBanner : Border
{
    /// <summary>Screenshot mode only: show the banner regardless of live conditions.</summary>
    public static bool ForceShow { get; set; }

    readonly AppServices _svc;
    readonly TextBlock _body;
    readonly TextBlock _steps;
    readonly Button _how;
    readonly Button _retry = new() { Content = "Take control again", Padding = new Thickness(12, 7, 12, 7), FontSize = 12, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBlock _title;
    DateTime _lastRead = DateTime.MinValue;
    (bool? autoFan, int? periodMs) _ab;

    public AfterburnerBanner(AppServices svc)
    {
        _svc = svc;
        SetResourceReference(BackgroundProperty, "WarnSoftBrush");
        SetResourceReference(BorderBrushProperty, "HairlineBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(12, 8, 8, 8);
        Margin = new Thickness(0, 0, 0, 12);
        Visibility = Visibility.Collapsed;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var ic = Icons.Icon("gpu", 16, "WarnBrush"); ic.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(ic);
        _title = B.Text("Afterburner owns the GPU fans", null);
        _title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_title, 1);
        grid.Children.Add(_title);
        _body = _title;   // the explanation lives in the tooltip

        _steps = B.Text("", "Muted", null, null, new Thickness(26, 6, 0, 4));
        _steps.TextWrapping = TextWrapping.Wrap; _steps.TextTrimming = TextTrimming.None;
        _steps.Visibility = Visibility.Collapsed;
        Grid.SetRow(_steps, 1); Grid.SetColumnSpan(_steps, 3);
        grid.Children.Add(_steps);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var show = new Button { Content = "Show", Style = B.S("GhostBtn"), ToolTip = "Bring MSI Afterburner to the front" };
        var folder = new Button { Content = "Config", Style = B.S("GhostBtn"), ToolTip = "Open Afterburner's profile folder" };
        var how = new Button { Content = "Fix", Style = B.S("GhostBtn"), ToolTip = "Steps to hand the GPU fans to VANTA" };
        _how = how;
        show.Click += (_, _) => AfterburnerHelper.ShowWindow();
        folder.Click += (_, _) => AfterburnerHelper.OpenConfigFolder();
        how.Click += (_, _) =>
        {
            bool open = _steps.Visibility != Visibility.Visible;
            _steps.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            how.Content = open ? "Hide" : "Fix";
        };
        _retry.Style = B.S("GhostBtn");
        _retry.Content = "Retake";
        _retry.Padding = new Thickness(8, 4, 8, 4); _retry.Margin = new Thickness(0);
        _retry.ToolTip = "After switching off Afterburner's fan control: let VANTA drive the GPU fans again.";
        _retry.Click += (_, _) => _svc.Ownership?.ResetGpuConflict();
        buttons.Children.Add(show); buttons.Children.Add(folder); buttons.Children.Add(how); buttons.Children.Add(_retry);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        Child = grid;
    }

    public void Update()
    {
        if ((DateTime.Now - _lastRead).TotalSeconds > 5)
        {
            _ab = ForceShow ? (true, 2000) : AfterburnerHelper.ReadFanSettings();
            _lastRead = DateTime.Now;
        }
        bool running = ForceShow || AfterburnerHelper.IsRunning;
        var own = _svc.Ownership?.Check(HwResource.GpuFans);
        bool withheld = own is { MayWrite: false };
        bool recentExternal = (DateTime.Now - _svc.Hub.Engine.LastExternalWrite).TotalSeconds < 90;
        bool show = ForceShow || (running && (withheld || recentExternal)) || (withheld && own!.Reason.StartsWith("another program"));
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        bool latched = own?.Reason.StartsWith("another program") == true;
        _title.Text = latched ? "Another program changed a GPU fan VANTA was driving" : "Afterburner owns the GPU fans";
        ToolTip = (withheld ? "VANTA is not writing to the GPU fans: " + own!.Reason + ". " : "") +
                  "Only one program can drive them. Switch off Afterburner's fan control, then " +
                  (latched ? "press Retake." : "VANTA takes them over within a few seconds.");
        _retry.Visibility = latched ? Visibility.Visible : Visibility.Collapsed;
        _steps.Text = AfterburnerHelper.HowToFix(_ab.periodMs);
    }
}
