namespace Butler.Tests;

// The real pipe (a local socket on macOS and Linux), under a throwaway name.
public class ShowSignalTests
{
    [Fact]
    public void A_second_start_reaches_the_running_tray_each_time()
    {
        var name = $"ButlerTest-{Guid.NewGuid()}";
        using var shown = new SemaphoreSlim(0);
        ShowSignal.Listen(() => shown.Release(), name);

        ShowSignal.Send(name);
        Assert.True(shown.Wait(5000));
        ShowSignal.Send(name); // the tray keeps listening
        Assert.True(shown.Wait(5000));
    }
}
