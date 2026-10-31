using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Butler;

// butler.ico (serving cloche), plus tray variants with a status dot. Read once Avalonia runs.
static class AppIcon
{
    const int Size = 32;
    public static WindowIcon App { get; } = new(Stream());
    public static WindowIcon Tray { get; } = App;
    public static WindowIcon TrayWaiting { get; } = WithDot(Color.FromRgb(240, 160, 30));
    public static WindowIcon TrayError { get; } = WithDot(Color.FromRgb(220, 60, 60));

    static Stream Stream() => typeof(AppIcon).Assembly.GetManifestResourceStream("Butler.butler.ico")!;

    static WindowIcon WithDot(Color color)
    {
        using var ico = new Bitmap(Stream());
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var g = bmp.CreateDrawingContext())
        {
            g.DrawImage(ico, new Rect(0, 0, Size, Size));
            var d = Size * 0.5;
            var dot = new Rect(Size - d, Size - d, d - 0.5, d - 0.5);
            g.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, Math.Max(1, Size / 16.0)), dot.Center, dot.Width / 2, dot.Height / 2);
        }
        return new WindowIcon(bmp);
    }
}
