// PORTED from src\HwSuite\Ui\FanActivationDialog.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
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

/// <summary>Confirmation shown before fan control is switched on (see <see cref="FanActivation"/>).</summary>
public sealed class FanActivationDialog : DialogWindow
{
    readonly List<FanActivation.Candidate> _candidates;
    readonly Dictionary<string, ToggleButton> _switches = new();
    readonly TextBlock _summary = B.Para("", null!);
    readonly AppConfig _cfg;
    public HashSet<string> Picked { get; private set; } = new();

    public FanActivationDialog(List<FanActivation.Candidate> candidates, AppConfig cfg)
    {
        _candidates = candidates;
        _cfg = cfg;
        B.Fg(_summary, "TextBrush");
        Title = "Turn on fan control";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this.SetResourceReference(BackgroundProperty, "CanvasBrush"); this.SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 680 };
        root.Children.Add(B.Text("Turn on fan control", "PageTitle"));
        root.Children.Add(B.Para("Fan control drives every fan that is not set to stay on firmware. Choose the fans VANTA should drive; the others stay with the motherboard BIOS or the GPU firmware. No curve is changed.",
            "Muted", new Thickness(0, 8, 0, 18)));

        if (candidates.Count == 0)
            root.Children.Add(B.Para("No fan can be driven yet: case fans and coolers need a confirmed minimum, unidentified headers need a type. Fan control will switch on without driving anything.", "Muted", new Thickness(0, 0, 0, 8)));
        var list = new Border { Classes = { "Card" }, Padding = new Thickness(0) };
        var listStack = new StackPanel();
        list.Child = listStack;
        foreach (var c in candidates)
        {
            var sw = new ToggleButton { Classes = { "Switch" }, IsChecked = c.DrivenNow, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            sw.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Drive " + c.Label);
            sw.Checked += (_, _) => UpdateSummary(); sw.Unchecked += (_, _) => UpdateSummary();
            _switches[c.Channel.Id] = sw;
            var text = new StackPanel();
            var n = FanNames.For(_cfg, c.Channel);
            text.Children.Add(B.Text(n.Primary, null, 14, FontWeight.SemiBold));
            string floor = c.Channel.Resource == HwResource.GpuFans ? $"NVML minimum {c.Safety.Floor:0}%"
                : c.Kind is FanKind.Aio or FanKind.Pump ? $"floor {c.Safety.Floor:0}% (pump policy)"
                : $"floor {c.Safety.Floor:0}%" + (c.Safety.Provisional ? " · restart not measured; VANTA verifies each start" : $" · {FanSafety.StartupNote(c.Safety)}");
            text.Children.Add(B.Para($"{n.Secondary} · {FanActivation.KindText(c.Kind)}", "Label", new Thickness(0, 1, 0, 0)));
            text.Children.Add(B.Para($"Now: {(c.DrivenNow ? $"set to follow its VANTA curve ({c.CurveName})" : "stays on firmware")} · {floor}", "Muted", new Thickness(0, 2, 0, 0)));
            var row = new DockPanel { Margin = new Thickness(0) };
            DockPanel.SetDock(sw, Dock.Left);
            row.Children.Add(sw); row.Children.Add(text);
            var cell = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, listStack.Children.Count == 0 ? 0 : 1, 0, 0), Child = row };
            B.Bd(cell, "HairlineBrush");
            listStack.Children.Add(cell);
        }
        if (candidates.Count > 0) root.Children.Add(list);
        _summary.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(_summary);

        var buttons = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = false };
        var caseOnly = new Button { Content = "Case fans only" };
        ToolTip.SetTip(caseOnly, "Tick only the case-fan headers; GPU fans and the AIO/pump stay with their firmware (their curves are kept for later).");
        caseOnly.IsEnabled = candidates.Any(c => c.Kind == FanKind.CaseFan);
        caseOnly.Click += (_, _) => { var pick = FanActivation.CaseFansOnly(_candidates); foreach (var (id, s) in _switches) s.IsChecked = pick.Contains(id); };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        var ok = new Button { Content = "Turn on fan control", Classes = { "PrimaryBtn" }, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) => { Picked = _switches.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet(); DialogResult = true; };
        DockPanel.SetDock(caseOnly, Dock.Left); DockPanel.SetDock(ok, Dock.Right); DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(caseOnly); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        var frame = new Border { BorderThickness = new Thickness(1), Child = root };
        B.Bd(frame, "HairlineBrush"); B.Bg(frame, "CanvasBrush");
        Content = frame;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        string N(FanActivation.Candidate c) => FanNames.For(_cfg, c.Channel).Primary;
        var on = _candidates.Where(c => _switches[c.Channel.Id].IsChecked == true).Select(N).ToList();
        var off = _candidates.Where(c => _switches[c.Channel.Id].IsChecked != true && c.DrivenNow).Select(N).ToList();
        _summary.Text = (on.Count == 0 ? "VANTA will drive no fan." : "VANTA will drive: " + string.Join(", ", on) + ".")
            + (off.Count > 0 ? " Back to firmware (curve kept): " + string.Join(", ", off) + "." : "");
    }
}
