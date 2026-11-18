using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;

namespace HwSuite.Ui;

/// <summary>
/// What switching fan control on would do, channel by channel. The global switch drives every channel whose mode is not
/// Firmware (Auto), so turning it on can also start existing GPU or pump curves. The owner picks the channels explicitly;
/// nothing else is changed: unpicked channels go to Firmware, picked ones on Firmware go to Curve, every curve is kept.
/// </summary>
public static class FanActivation
{
    public sealed record Candidate(IFanChannel Channel, string Label, FanKind Kind, ChannelMode Mode, string CurveName, FanSafety.Assessment Safety)
    {
        /// <summary>Would be driven if fan control were switched on now, unchanged.</summary>
        public bool DrivenNow => Mode != ChannelMode.Auto;
    }

    /// <summary>Every channel VANTA is allowed to drive (evidence-backed floor), in engine order. Others cannot be picked.</summary>
    public static List<Candidate> Candidates(AppConfig cfg, IEnumerable<IFanChannel> channels)
    {
        var list = new List<Candidate>();
        lock (cfg.Sync)
            foreach (var ch in channels)
            {
                var cc = cfg.Channels.FirstOrDefault(c => c.Id == ch.Id);
                if (cc == null) continue;
                var a = FanSafety.Assess(cc, ch);
                if (!a.Controllable) continue;
                list.Add(new Candidate(ch, string.IsNullOrWhiteSpace(cc.Label) ? ch.Name : cc.Label, cc.Kind, cc.Mode, cc.Curve.Name, a));
            }
        return list;
    }

    public static HashSet<string> CaseFansOnly(IEnumerable<Candidate> c) => c.Where(x => x.Kind == FanKind.CaseFan).Select(x => x.Channel.Id).ToHashSet();

    /// <summary>Apply the owner's pick to the channel modes (curves untouched). Returns one line per changed channel.</summary>
    public static List<string> Apply(AppConfig cfg, IEnumerable<Candidate> candidates, ISet<string> picked)
    {
        var changes = new List<string>();
        lock (cfg.Sync)
            foreach (var c in candidates)
            {
                var cc = cfg.Channels.First(x => x.Id == c.Channel.Id);
                bool want = picked.Contains(c.Channel.Id);
                if (want && cc.Mode == ChannelMode.Auto) { cc.Mode = ChannelMode.Curve; changes.Add($"{c.Label}: Firmware -> Curve ({cc.Curve.Name})"); }
                else if (!want && cc.Mode != ChannelMode.Auto) { changes.Add($"{c.Label}: {cc.Mode} -> Firmware (curve kept)"); cc.Mode = ChannelMode.Auto; }
            }
        return changes;
    }

    public static string KindText(FanKind k) => k switch
    {
        FanKind.CaseFan => "case fan", FanKind.GpuFan => "GPU fan", FanKind.Aio => "AIO (pump + radiator fans)", FanKind.Pump => "pump",
        FanKind.CpuCooler => "CPU cooler", _ => "header"
    };
}

/// <summary>Confirmation shown before fan control is switched on (see <see cref="FanActivation"/>).</summary>
public sealed class FanActivationDialog : Window
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
        _summary.Style = null; B.Fg(_summary, "TextBrush");
        Title = "Turn on fan control";
        WindowStyle = WindowStyle.ToolWindow; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 680 };
        root.Children.Add(B.Text("Turn on fan control", "PageTitle"));
        root.Children.Add(B.Para("Fan control drives every fan that is not set to stay on firmware. Choose the fans VANTA should drive; the others stay with the motherboard BIOS or the GPU firmware. No curve is changed.",
            "Muted", new Thickness(0, 8, 0, 18)));

        if (candidates.Count == 0)
            root.Children.Add(B.Para("No fan can be driven yet: case fans and coolers need a confirmed minimum, unidentified headers need a type. Fan control will switch on without driving anything.", "Muted", new Thickness(0, 0, 0, 8)));
        var list = new Border { Style = B.S("Card"), Padding = new Thickness(0) };
        var listStack = new StackPanel();
        list.Child = listStack;
        foreach (var c in candidates)
        {
            var sw = new ToggleButton { Style = B.S("Switch"), IsChecked = c.DrivenNow, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            sw.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Drive " + c.Label);
            sw.Checked += (_, _) => UpdateSummary(); sw.Unchecked += (_, _) => UpdateSummary();
            _switches[c.Channel.Id] = sw;
            var text = new StackPanel();
            var n = FanNames.For(_cfg, c.Channel);
            text.Children.Add(B.Text(n.Primary, null, 14, FontWeights.SemiBold));
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
        caseOnly.ToolTip = "Tick only the case-fan headers; GPU fans and the AIO/pump stay with their firmware (their curves are kept for later).";
        caseOnly.IsEnabled = candidates.Any(c => c.Kind == FanKind.CaseFan);
        caseOnly.Click += (_, _) => { var pick = FanActivation.CaseFansOnly(_candidates); foreach (var (id, s) in _switches) s.IsChecked = pick.Contains(id); };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        var ok = new Button { Content = "Turn on fan control", Style = B.S("PrimaryBtn"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
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
