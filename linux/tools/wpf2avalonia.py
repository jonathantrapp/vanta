"""
First-pass mechanical conversion of the Windows VANTA UI (WPF, built in C# code) to Avalonia 11.
Usage: python wpf2avalonia.py <src-file> <dst-file>
Only the routine, unambiguous API differences are rewritten here; everything else is fixed by hand afterwards.
The output starts with a // PORTED header so linux/check-drift.sh tracks the original.
"""
import re, sys, os

AV_USINGS = [
    'using Avalonia;', 'using Avalonia.Controls;', 'using Avalonia.Controls.Documents;', 'using Avalonia.Controls.Primitives;',
    'using Avalonia.Controls.Shapes;', 'using Avalonia.Input;', 'using Avalonia.Interactivity;', 'using Avalonia.Layout;',
    'using Avalonia.Media;', 'using Avalonia.Media.Imaging;', 'using Avalonia.Threading;',
]

def statement_end(s: str, i: int) -> int:
    """Index of the ';' ending the expression that starts at i (skips string literals and nested brackets)."""
    depth, n = 0, len(s)
    while i < n:
        c = s[i]
        if c == '"':
            verbatim = i > 0 and s[i - 1] == '@'
            i += 1
            while i < n and s[i] != '"':
                if s[i] == '\\' and not verbatim: i += 1
                i += 1
        elif c == "'":
            i += 1
            while i < n and s[i] != "'":
                if s[i] == '\\': i += 1
                i += 1
        elif c in '([{': depth += 1
        elif c in ')]}': depth -= 1
        elif c == ';' and depth == 0: return i
        i += 1
    return -1

def tooltips(s: str) -> str:
    """x.ToolTip = expr;  ->  ToolTip.SetTip(x, expr);  (statement form only; initializers are fixed by hand)."""
    out, pos = [], 0
    for m in re.finditer(r'(?<![\w\.])([\w\.\[\]]+)\.ToolTip\s*=\s*', s):
        if m.start() < pos: continue
        end = statement_end(s, m.end())
        if end < 0: continue
        out.append(s[pos:m.start()])
        out.append(f'ToolTip.SetTip({m.group(1)}, {s[m.end():end]});')
        pos = end + 1
    out.append(s[pos:])
    return ''.join(out)

def convert(s: str) -> str:
    lines = s.splitlines()
    out, had_wpf = [], False
    for l in lines:
        if re.match(r'\s*using (System\.Windows|Microsoft\.Win32|WinForms|Drawing)\b', l) or re.match(r'\s*using \w+ = System\.(Windows|Drawing)', l):
            had_wpf = True
            continue
        out.append(l)
    s = '\n'.join(out) + '\n'
    if had_wpf:
        # put Avalonia usings after the last remaining using
        idx = [m.end() for m in re.finditer(r'^using [^\n]+;\n', s, re.M)]
        at = idx[-1] if idx else 0
        s = s[:at] + '\n'.join(AV_USINGS) + '\nusing Path = Avalonia.Controls.Shapes.Path;\n' + s[at:]

    rep = [
        # visibility
        (r'Visibility\s*=\s*([^;,{}]+?)\s*\?\s*Visibility\.Visible\s*:\s*Visibility\.Collapsed', r'IsVisible = \1'),
        (r'Visibility\s*=\s*([^;,{}]+?)\s*\?\s*Visibility\.Collapsed\s*:\s*Visibility\.Visible', r'IsVisible = !(\1)'),
        (r'Visibility\s*=\s*([^;,{}]+?)\s*\?\s*Visibility\.Visible\s*:\s*Visibility\.Hidden', r'IsVisible = \1'),
        (r'Visibility\s*=\s*Visibility\.Visible', 'IsVisible = true'),
        (r'Visibility\s*=\s*Visibility\.(Collapsed|Hidden)', 'IsVisible = false'),
        (r'\.Visibility\s*==\s*Visibility\.Visible', '.IsVisible'),
        (r'\.Visibility\s*!=\s*Visibility\.Visible', '.IsVisible == false'),
        (r'\.Visibility\s*==\s*Visibility\.Collapsed', '.IsVisible == false'),
        # fonts
        (r'\bFontWeights\.', 'FontWeight.'), (r'\bFontStyles\.', 'FontStyle.'), (r'\bFontStretches\.', 'FontStretch.'),
        # styles -> style classes
        (r'\bStyle\s*=\s*(?:B\.)?S\("([\w]+)"\)', r'Classes = { "\1" }'),
        (r'(\w[\w\.\]\)]*)\.Style\s*=\s*(?:B\.)?S\(([^)]+)\);', r'\1.Classes.Add(\2);'),
        # element types
        (r'\bFrameworkElement\b', 'Control'), (r'\bUIElement\b', 'Control'),
        (r'\bActualWidth\b', 'Bounds.Width'), (r'\bActualHeight\b', 'Bounds.Height'),
        # input
        (r'\bMouseLeftButtonDown\b', 'PointerPressed'), (r'\bPreviewMouseLeftButtonDown\b', 'PointerPressed'),
        (r'\bMouseLeftButtonUp\b', 'PointerReleased'), (r'\bMouseEnter\b', 'PointerEntered'), (r'\bMouseLeave\b', 'PointerExited'),
        (r'\bMouseMove\b', 'PointerMoved'), (r'\bMouseWheel\b', 'PointerWheelChanged'),
        (r'\bMouseButtonEventArgs\b', 'PointerPressedEventArgs'), (r'\bMouseEventArgs\b', 'PointerEventArgs'),
        (r'Cursor\s*=\s*Cursors\.Hand', 'Cursor = new Cursor(StandardCursorType.Hand)'),
        (r'Cursor\s*=\s*Cursors\.SizeAll', 'Cursor = new Cursor(StandardCursorType.SizeAll)'),
        (r'Cursor\s*=\s*Cursors\.Arrow', 'Cursor = new Cursor(StandardCursorType.Arrow)'),
        (r'Cursor\s*=\s*null', 'Cursor = Cursor.Default'),
        # toggles
        # threading
        (r'\bDispatcher\.BeginInvoke\(', 'Dispatcher.UIThread.Post('), (r'\bDispatcher\.Invoke\(', 'Dispatcher.UIThread.Invoke('),
        (r'Application\.Current\.Dispatcher\.', 'Dispatcher.UIThread.'),
        (r'\bDispatcherPriority\.Background\b', 'DispatcherPriority.Background'),
        (r'\bDispatcherPriority\.ApplicationIdle\b', 'DispatcherPriority.ApplicationIdle'),
        # focus / input overrides
        (r'\bIsKeyboardFocused\b', 'IsFocused'), (r'\bIsKeyboardFocusWithin\b', 'IsKeyboardFocusWithin'),
        (r'protected override void OnGotKeyboardFocus\(KeyboardFocusChangedEventArgs e\)', 'protected override void OnGotFocus(GotFocusEventArgs e)'),
        (r'base\.OnGotKeyboardFocus\(e\)', 'base.OnGotFocus(e)'),
        (r'protected override void OnLostKeyboardFocus\(KeyboardFocusChangedEventArgs e\)', 'protected override void OnLostFocus(RoutedEventArgs e)'),
        (r'base\.OnLostKeyboardFocus\(e\)', 'base.OnLostFocus(e)'),
        (r'protected override void OnMouseDown\((?:MouseButtonEventArgs|PointerPressedEventArgs) e\)', 'protected override void OnPointerPressed(PointerPressedEventArgs e)'),
        (r'base\.OnMouseDown\(e\)', 'base.OnPointerPressed(e)'),
        (r'protected override void OnMouseUp\((?:MouseButtonEventArgs|PointerPressedEventArgs) e\)', 'protected override void OnPointerReleased(PointerReleasedEventArgs e)'),
        (r'base\.OnMouseUp\(e\)', 'base.OnPointerReleased(e)'),
        (r'protected override void OnMouseMove\((?:MouseEventArgs|PointerEventArgs) e\)', 'protected override void OnPointerMoved(PointerEventArgs e)'),
        (r'base\.OnMouseMove\(e\)', 'base.OnPointerMoved(e)'),
        (r'protected override void OnMouseLeave\((?:MouseEventArgs|PointerEventArgs) e\)', 'protected override void OnPointerExited(PointerEventArgs e)'),
        (r'base\.OnMouseLeave\(e\)', 'base.OnPointerExited(e)'),
        (r'protected override void OnMouseEnter\((?:MouseEventArgs|PointerEventArgs) e\)', 'protected override void OnPointerEntered(PointerEventArgs e)'),
        (r'base\.OnMouseEnter\(e\)', 'base.OnPointerEntered(e)'),
        (r'protected override void OnLostMouseCapture\((?:MouseEventArgs|PointerEventArgs) e\)', 'protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)'),
        (r'base\.OnLostMouseCapture\(e\)', 'base.OnPointerCaptureLost(e)'),
        (r'[\w\.]+\.FocusVisualStyle\s*=\s*null;', ''), (r'\bFocusVisualStyle\s*=\s*null;?', ''),
        # brushes as types -> IBrush (method names like Themes.Brush( are untouched)
        (r'\bBrush(\??)(\s+)(?=[A-Za-z_])', r'IBrush\1\2'), (r'<Brush>', '<IBrush>'), (r'\(Brush\??\)', '(IBrush)'),
        (r'\bnew FormattedText\(', 'Fx.Text('), (r'\bFormattedText\s+Text\(', 'FormattedText Text('),
        # dependency properties
        (r'public static readonly DependencyProperty (\w+)Property = DependencyProperty\.Register\(\s*nameof\((\w+)\),\s*typeof\(([^)]+)\),\s*typeof\((\w+)\),\s*new FrameworkPropertyMetadata\(([^,]+?),\s*FrameworkPropertyMetadataOptions\.AffectsRender\)\);',
         r'public static readonly StyledProperty<\3> \1Property = WpfCompat.Register<\4, \3>(nameof(\2), \5);'),
        # fonts
        (r'"Segoe UI Variable Display, Segoe UI"', 'Fonts.Display'), (r'"Segoe UI Variable Text, Segoe UI"', 'Fonts.Ui'),
        (r'"Segoe UI Variable Display"', 'Fonts.Display'), (r'"Segoe UI"', 'Fonts.Ui'), (r'"Cascadia Mono, Consolas"', 'Fonts.Mono'),
        (r'System\.Windows\.Shapes\.', ''), (r'System\.Windows\.Controls\.Primitives\.', ''), (r'System\.Windows\.Controls\.', ''),
        (r'System\.Windows\.Input\.ModifierKeys', 'KeyModifiers'), (r'System\.Windows\.Input\.', ''), (r'System\.Windows\.Media\.Imaging\.', ''),
        (r'System\.Windows\.Media\.', ''), (r'System\.Windows\.Threading\.', ''), (r'System\.Windows\.', ''),
        (r'\bAutomation\.AutomationProperties\b', 'Avalonia.Automation.AutomationProperties'),
        (r'(?<![\w\.])AutomationProperties\.', 'Avalonia.Automation.AutomationProperties.'),
        (r'new Data\.Binding\(("\w+")\) \{ RelativeSource = new Data\.RelativeSource\(Data\.RelativeSourceMode\.FindAncestor, (\w+), 1\) \}',
         r'new Avalonia.Data.Binding(\1) { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = \2 } }'),
        (r'\.SetBinding\(', '.Bind('),
        (r'([\{,]\s*)ToolTip\s*=\s*', r'\1[ToolTip.TipProperty] = '),
        (r'\bWindow\.GetWindow\(', 'WpfCompat.WindowOf('),
        (r'(?<![\w\.])SetResourceReference\(', 'this.SetResourceReference('),
        # misc
        (r'[\w\.\]\)]+\.Freeze\(\);\s*', ''),
        (r'Panel\.SetZIndex\((\w+),\s*([^)]+)\)', r'\1.ZIndex = \2'),
        (r'\bTextOptions\.[^;]+;', ''),
        (r'\bRenderOptions\.SetBitmapScalingMode\([^;]+;', ''),
        (r'\bSnapsToDevicePixels\s*=\s*true,?', ''), (r'\bUseLayoutRounding\s*=\s*true,?', ''),
        (r'\bFocusable\b', 'Focusable'),
        (r'protected override void OnRender\(DrawingContext (\w+)\)', r'public override void Render(DrawingContext \1)'),
        (r'base\.OnRender\((\w+)\)', r'base.Render(\1)'),
    ]
    for a, b in rep:
        s = re.sub(a, b, s)
    # x.ToolTip = expr;  ->  ToolTip.SetTip(x, expr);
    s = tooltips(s)
    s = re.sub(r'([\w\.\[\]]+)\.Classes = \{ ("[\w]+") \};', r'\1.Classes.Add(\2);', s)
    return s

if __name__ == '__main__':
    src, dst = sys.argv[1], sys.argv[2]
    s = open(src, encoding='utf-8-sig').read()
    rel = os.path.relpath(src, r'.').replace('/', '\\')
    hdr = f'// PORTED from {rel} (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).\n'
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    out = hdr + convert(s)          # convert first: a failure must not truncate an existing file
    open(dst, 'w', encoding='utf-8').write(out)
