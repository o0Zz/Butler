using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Butler;

// The tray icon as Avalonia draws it (Skia, headless), on the session's UI thread.
public class AppIconTests
{
    sealed class App : Application;

    static readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(AppIconTests));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    static T OnUi<T>(Func<T> f) => session.Dispatch(f, CancellationToken.None).GetAwaiter().GetResult();

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

    static (byte B, byte G, byte R, byte A)[] Pixels(RenderTargetBitmap bmp)
    {
        var bytes = new byte[32 * 32 * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, 32, 32), handle.AddrOfPinnedObject(), bytes.Length, 32 * 4); }
        finally { handle.Free(); }
        return Enumerable.Range(0, 32 * 32).Select(i => (bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3])).ToArray();
    }
}
