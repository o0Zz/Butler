using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Butler;

// butler.ico (serving cloche), plus tray variants: a red dot for an error, a red badge with the number of runs waiting.
// Read once Avalonia runs.
static class AppIcon
{
    const int Size = 32;
    static readonly Color Red = Color.FromRgb(220, 60, 60);
    static readonly Dictionary<string, WindowIcon> waiting = [];
    public static WindowIcon App { get; } = new(Stream());
    public static WindowIcon Tray { get; } = App;
    public static WindowIcon TrayError { get; } = new(WithBadge(null));

    // The tray icon with the count in its badge (9+ above 9).
    public static WindowIcon TrayWaiting(int count)
    {
        var text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
        if (!waiting.TryGetValue(text, out var icon)) waiting[text] = icon = new WindowIcon(WithBadge(text));
        return icon;
    }

    static Stream Stream() => typeof(AppIcon).Assembly.GetManifestResourceStream("Butler.butler.ico")!;

    internal static RenderTargetBitmap WithBadge(string? text)
    {
        using var ico = new Bitmap(Stream());
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var g = bmp.CreateDrawingContext())
        {
            g.DrawImage(ico, new Rect(0, 0, Size, Size));
            var d = Size * (text == null ? 0.5 : 0.6);
            var dot = new Rect(Size - d, Size - d, d - 0.5, d - 0.5);
            g.DrawEllipse(new SolidColorBrush(Red), new Pen(Brushes.White, Math.Max(1, Size / 16.0)), dot.Center, dot.Width / 2, dot.Height / 2);
            if (text != null)
            {
                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, weight: FontWeight.Bold), text.Length > 1 ? Size * 0.3 : Size * 0.4, Brushes.White);
                g.DrawText(label, dot.Center - new Point(label.Width / 2, label.Height / 2));
            }
        }
        return bmp;
    }
}
