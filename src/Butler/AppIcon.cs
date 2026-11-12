using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Butler;

// butler.ico (serving cloche), plus tray variants: a red dot for an error, a red badge with the number of runs waiting.
// The badge alone is the taskbar's (IShell.Badge).
// Read once Avalonia runs.
static class AppIcon
{
    const int Size = 32;
    static readonly Color Red = Color.FromRgb(220, 60, 60);
    static readonly Dictionary<string, WindowIcon> waiting = [];
    static readonly Dictionary<string, byte[]> badges = [];
    public static WindowIcon App { get; } = new(Stream());
    public static WindowIcon Tray { get; } = App;
    public static WindowIcon TrayError { get; } = new(WithBadge(null));

    // The tray icon with the count in its badge (9+ above 9).
    public static WindowIcon TrayWaiting(int count)
    {
        var text = Count(count);
        if (!waiting.TryGetValue(text, out var icon)) waiting[text] = icon = new WindowIcon(WithBadge(text));
        return icon;
    }

    public static string Count(int count) => count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    // The badge alone, filling the image, as a PNG: "" for the dot. Drawn once per label.
    public static byte[] BadgePng(string label)
    {
        if (badges.TryGetValue(label, out var bytes)) return bytes;
        using var png = new MemoryStream();
        WithBadge(label == "" ? null : label, icon: false).Save(png, new PngBitmapEncoderOptions());
        return badges[label] = png.ToArray();
    }

    static Stream Stream() => typeof(AppIcon).Assembly.GetManifestResourceStream("Butler.butler.ico")!;

    internal static RenderTargetBitmap WithBadge(string? text, bool icon = true)
    {
        using var ico = new Bitmap(Stream());
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var g = bmp.CreateDrawingContext())
        {
            if (icon) g.DrawImage(ico, new Rect(0, 0, Size, Size));
            var d = icon ? Size * (text == null ? 0.5 : 0.6) : Size;
            var dot = new Rect(Size - d, Size - d, d - 0.5, d - 0.5);
            g.DrawEllipse(new SolidColorBrush(Red), new Pen(Brushes.White, Math.Max(1, Size / 16.0)), dot.Center, dot.Width / 2, dot.Height / 2);
            if (text != null)
            {
                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, weight: FontWeight.Bold), text.Length > 1 ? d / 2 : d * 2 / 3, Brushes.White);
                g.DrawText(label, dot.Center - new Point(label.Width / 2, label.Height / 2));
            }
        }
        return bmp;
    }
}
