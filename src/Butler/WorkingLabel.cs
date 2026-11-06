using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Butler;

// "fix-text: working..." by the mouse pointer while a hotkey trigger runs, its dots moving. Shown without activation:
// the window the answer is pasted into must stay in front.
sealed class WorkingLabel : Window
{
    readonly DispatcherTimer dots = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public WorkingLabel(string text, int x, int y)
    {
        WindowDecorations = WindowDecorations.None;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Position = new PixelPoint(x + 16, y + 20); // below right of the pointer, as a tooltip
        var label = new TextBlock { Text = text + "…", Foreground = Brushes.White, FontSize = 13, MinWidth = 140 };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6),
            IsHitTestVisible = false,
            Child = label,
        };
        var n = 3;
        dots.Tick += (_, _) => label.Text = text + new string('.', n = n % 3 + 1);
        dots.Start();
        Closed += (_, _) => dots.Stop();
    }
}
