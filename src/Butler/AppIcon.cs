namespace Butler;

// butler.ico (serving cloche), plus tray variants with a status dot.
static class AppIcon
{
    public static Icon App { get; } = Load(32);
    public static Icon Tray { get; } = Load(SystemInformation.SmallIconSize.Width);
    public static Icon TrayWaiting { get; } = WithDot(Color.FromArgb(240, 160, 30));
    public static Icon TrayError { get; } = WithDot(Color.FromArgb(220, 60, 60));

    static Icon Load(int size)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("Butler.butler.ico")!;
        return new Icon(s, size, size);
    }

    static Icon WithDot(Color color)
    {
        using var bmp = Tray.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = bmp.Width * 0.5f;
            var dot = new RectangleF(bmp.Width - d, bmp.Height - d, d - 0.5f, d - 0.5f);
            using var fill = new SolidBrush(color);
            using var ring = new Pen(Color.White, Math.Max(1f, bmp.Width / 16f));
            g.FillEllipse(fill, dot);
            g.DrawEllipse(ring, dot);
        }
        return Icon.FromHandle(bmp.GetHicon()); // created once per variant, so the handle is never released on purpose
    }
}
