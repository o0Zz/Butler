using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Butler;

// The tray icon as Avalonia draws it (Skia, headless), on the session's UI thread.
public class AppIconTests
{
    sealed class App : Application;

    static readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(AppIconTests));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    internal static T OnUi<T>(Func<T> f) => session.Dispatch(f, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void Waiting_icon_has_a_red_badge_with_the_count_in_white() => OnUi(() =>
    {
        var px = Pixels(AppIcon.WithBadge("3"));
        // The badge fills the bottom-right corner: red around the digit, white in it.
        var badge = Enumerable.Range(14, 18).SelectMany(y => Enumerable.Range(14, 18).Select(x => px[y * 32 + x])).ToList();
        Assert.Contains(badge, p => p.R > 200 && p.G < 90 && p.B < 90);
        Assert.Contains(badge.Skip(5 * 18).Take(8 * 18), p => p.R > 230 && p.G > 230 && p.B > 230);
        Assert.NotEqual(Pixels(AppIcon.WithBadge("3")), Pixels(AppIcon.WithBadge("9+")));
        return 0;
    });

    [Fact]
    public void Waiting_icon_is_built_once_per_count_and_tops_at_9() => OnUi(() =>
    {
        Assert.Same(AppIcon.TrayWaiting(2), AppIcon.TrayWaiting(2));
        Assert.Same(AppIcon.TrayWaiting(10), AppIcon.TrayWaiting(42));
        Assert.NotSame(AppIcon.TrayWaiting(2), AppIcon.TrayWaiting(3));
        return 0;
    });

    // The taskbar's badge: the red dot alone, filling the image (transparent corners), the count in white.
    [Fact]
    public void Taskbar_badge_is_the_red_dot_alone() => OnUi(() =>
    {
        using var png = new Bitmap(new MemoryStream(AppIcon.BadgePng("3")));
        var bmp = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var g = bmp.CreateDrawingContext()) g.DrawImage(png, new Rect(0, 0, 32, 32));
        var px = Pixels(bmp);
        Assert.Equal(0, px[0].A);
        Assert.Contains(px, p => p.R > 200 && p.G < 90 && p.B < 90 && p.A == 255);
        Assert.Contains(px.Skip(10 * 32).Take(12 * 32), p => p.R > 230 && p.G > 230 && p.B > 230);
        Assert.NotEqual(AppIcon.BadgePng("3"), AppIcon.BadgePng(""));
        return 0;
    });

    // In the bitmap's own byte order: BGRA on Windows, RGBA on macOS.
    static (byte R, byte G, byte B, byte A)[] Pixels(RenderTargetBitmap bmp)
    {
        var bytes = new byte[32 * 32 * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, 32, 32), handle.AddrOfPinnedObject(), bytes.Length, 32 * 4); }
        finally { handle.Free(); }
        var (r, b) = bmp.Format == PixelFormat.Rgba8888 ? (0, 2) : bmp.Format == PixelFormat.Bgra8888 ? (2, 0)
            : throw new NotSupportedException($"Pixel format {bmp.Format}");
        return Enumerable.Range(0, 32 * 32).Select(i => (bytes[i * 4 + r], bytes[i * 4 + 1], bytes[i * 4 + b], bytes[i * 4 + 3])).ToArray();
    }
}
