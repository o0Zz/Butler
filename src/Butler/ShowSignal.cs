using System.IO.Pipes;

namespace Butler;

// A second start of Butler brings up the running tray's dashboard, through a named pipe (a local socket on macOS
// and Linux): the tray listens, the second start connects and quits.
public static class ShowSignal
{
    // Short: on macOS the socket lives in a long temp folder, and a socket path is at most 104 characters.
    static string Pipe(string? name) => name ?? $"Butler.{Environment.UserName}";

    // Calls show at each connection, on a background thread, for the life of the process.
    public static void Listen(Action show, string? name = null) => new Thread(() =>
    {
        while (true)
        {
            // Closed before show: on macOS and Linux a start arriving meanwhile would land on this socket and be lost.
            using (var server = new NamedPipeServerStream(Pipe(name))) server.WaitForConnection();
            show();
        }
    }) { IsBackground = true }.Start();

    public static void Send(string? name = null)
    {
        using var client = new NamedPipeClientStream(Pipe(name));
        client.Connect(5000);
    }
}
