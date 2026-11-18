// LINUX: the layout from src\HwSuite\Ui\MainWindow.xaml, built in code for Avalonia (same structure, names and sizes).
// Window chrome: Windows VANTA draws its own caption buttons (WindowChrome). On Fedora the desktop's own title bar is
// kept (GNOME/KDE decorations, native resize and snapping), so the custom caption buttons exist but stay hidden.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace HwSuite.Ui;

public partial class MainWindow
{
    Grid Root = null!;
    RadioButton NavDashboard = null!, NavFans = null!, NavLighting = null!, NavAl80 = null!, NavSystem = null!;
    ToggleButton EngineSwitch = null!;
    TextBlock EngineState = null!, MachineLine = null!, PageTitleText = null!, FixtureText = null!, PageStatusText = null!;
    Button ReleaseButton = null!, MinButton = null!, MaxButton = null!, CloseButton = null!;
    Border FixtureBadge = null!;
    StackPanel HeaderActions = null!;
    ContentControl PageHost = null!;

    static T Res<T>(T el, AvaloniaProperty p, string key) where T : StyledElement { el.SetResourceReference(p, key); return el; }

    void InitializeComponent()
    {
        Title = "VANTA";
        Width = 1440; Height = 900; MinWidth = 1180; MinHeight = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://vanta/Assets/vanta.ico"))); } catch { }
        this.SetResourceReference(BackgroundProperty, "CanvasBrush");
        this.SetResourceReference(ForegroundProperty, "TextBrush");

        Root = new Grid { ColumnDefinitions = new ColumnDefinitions("216,*") };
        Res(Root, Panel.BackgroundProperty, "CanvasBrush");

        // ---- sidebar
        var side = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        Res(side, Border.BackgroundProperty, "SidebarBrush"); Res(side, Border.BorderBrushProperty, "HairlineBrush");
        var sg = new Grid { Margin = new Thickness(10, 0), RowDefinitions = new RowDefinitions("48,Auto,*,Auto") };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var mark = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0,0 L6,12 L12,0"), StrokeThickness = 2.2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0) };
        Res(mark, Shape.StrokeProperty, "AccentBrush");
        brand.Children.Add(mark);
        brand.Children.Add(Res(new TextBlock { Text = "VANTA", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "TextBrush"));
        sg.Children.Add(brand);

        var nav = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        RadioButton Nav(string tag, bool first = false) { var r = new RadioButton { GroupName = "nav", Tag = tag, IsChecked = first, Classes = { "NavItem" } }; nav.Children.Add(r); return r; }
        NavDashboard = Nav("dashboard", true); NavFans = Nav("fans"); NavLighting = Nav("lighting"); NavAl80 = Nav("al80"); NavSystem = Nav("system");
        Grid.SetRow(nav, 1); sg.Children.Add(nav);

        MachineLine = new TextBlock { Classes = { "Label" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 10), Text = "B660M DS3H" };
        Grid.SetRow(MachineLine, 2); sg.Children.Add(MachineLine);

        var fanCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 12, 12, 10), Margin = new Thickness(0, 0, 0, 12) };
        Res(fanCard, Border.BackgroundProperty, "PanelBrush"); Res(fanCard, Border.BorderBrushProperty, "HairlineBrush");
        var fcs = new StackPanel();
        var fch = new Grid();
        fch.Children.Add(new TextBlock { Text = "Fan control", Classes = { "CardTitle" }, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        EngineSwitch = new ToggleButton { Classes = { "Switch" }, HorizontalAlignment = HorizontalAlignment.Right };
        ToolTip.SetTip(EngineSwitch, "On: VANTA drives the fans you chose. Off: every fan follows its BIOS or GPU firmware.");
        Avalonia.Automation.AutomationProperties.SetName(EngineSwitch, "Fan control");
        fch.Children.Add(EngineSwitch);
        fcs.Children.Add(fch);
        EngineState = new TextBlock { Classes = { "Muted" }, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Text = "Firmware controls every fan" };
        fcs.Children.Add(EngineState);
        ReleaseButton = new Button { Content = "Release all fans", Classes = { "GhostBtn" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 4, 0, 0) };
        ToolTip.SetTip(ReleaseButton, "Hand every fan back to its firmware now and switch fan control off.");
        fcs.Children.Add(ReleaseButton);
        fanCard.Child = fcs;
        Grid.SetRow(fanCard, 3); sg.Children.Add(fanCard);
        side.Child = sg;
        Root.Children.Add(side);

        // ---- content column
        var content = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        Grid.SetColumn(content, 1);
        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        Res(header, Border.BorderBrushProperty, "HairlineBrush");
        var hg = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        PageTitleText = new TextBlock { Classes = { "PageTitle" }, Text = "Dashboard", VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(PageTitleText);
        FixtureBadge = new Border { IsVisible = false, Margin = new Thickness(16, 0, 0, 0), CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center };
        Res(FixtureBadge, Border.BackgroundProperty, "WarnSoftBrush");
        FixtureText = Res(new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Text = "Simulated fixture" }, TextBlock.ForegroundProperty, "WarnBrush");
        FixtureBadge.Child = FixtureText;
        titleRow.Children.Add(FixtureBadge);
        PageStatusText = new TextBlock { Classes = { "MonoMuted" }, Margin = new Thickness(16, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left };
        titleRow.Children.Add(PageStatusText);
        hg.Children.Add(titleRow);
        HeaderActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(HeaderActions, 1); hg.Children.Add(HeaderActions);
        var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, IsVisible = false };
        Button Cap(string data, string tip, string cls) { var b = new Button { Classes = { cls }, Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 1, Stroke = Brushes.Gray } }; ToolTip.SetTip(b, tip); caption.Children.Add(b); return b; }
        MinButton = Cap("M0,0.5 H10", "Minimize", "CaptionBtn"); MaxButton = Cap("M0.5,0.5 H9.5 V9.5 H0.5 Z", "Maximize", "CaptionBtn"); CloseButton = Cap("M0,0 L10,10 M10,0 L0,10", "Close to tray", "CloseBtn");
        Grid.SetColumn(caption, 2); hg.Children.Add(caption);
        header.Child = hg;
        content.Children.Add(header);
        PageHost = new ContentControl { Focusable = false, Margin = new Thickness(24, 16, 24, 20) };
        Grid.SetRow(PageHost, 1); content.Children.Add(PageHost);
        Root.Children.Add(content);
        Content = Root;
    }
}
