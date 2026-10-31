using Avalonia.Controls;
using Avalonia.Platform;

namespace Butler;

// Edge WebView2.
public sealed class WebView2 : IWebView
{
    public void Configure(WebViewEnvironmentRequestedEventArgs engine, string dataFolder) =>
        ((WindowsWebView2EnvironmentRequestedEventArgs)engine).UserDataFolder = dataFolder;
}
