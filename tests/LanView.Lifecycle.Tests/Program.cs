using System.Diagnostics;
using LanView.Windows;
using LanView.Windows.ClipboardIntegration;

if (args is ["--child-eof"])
{
    await Console.In.ReadToEndAsync();
    return 0;
}
if (args is ["--child-wait"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var passed = 0;
var failed = 0;

await Run("Repeated disposal before startup is safe and prevents a later start", async () =>
{
    var monitor = Monitor();
    var first = monitor.DisposeAsync().AsTask();
    var second = monitor.DisposeAsync().AsTask();
    Require(ReferenceEquals(first, second), "Disposal did not share one completion task.");
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    try
    {
        await monitor.StartAsync(CancellationToken.None);
        throw new Exception("A disposed monitor restarted.");
    }
    catch (ObjectDisposedException) { }
});

await Run("Canceled startup does not start the clipboard thread", async () =>
{
    await using var monitor = Monitor();
    try
    {
        await monitor.StartAsync(new CancellationToken(canceled: true));
        throw new Exception("Canceled startup succeeded.");
    }
    catch (OperationCanceledException) { }
});

await Run("Remote EOF and session cleanup can stop the monitor together", async () =>
{
    // The reader's EOF path and the controller used to queue separate ExitThread callbacks.
    // The first stopped the pump; the second callback never ran, leaving shutdown waiting forever.
    for (var iteration = 0; iteration < 100; iteration++)
    {
        var monitor = Monitor();
        await monitor.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var eof = monitor.DisposeAsync().AsTask();
        var disconnect = monitor.DisposeAsync().AsTask();
        Require(ReferenceEquals(eof, disconnect), "Concurrent shutdown created two stop tasks.");
        await Task.WhenAll(eof, disconnect).WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.DisposeAsync();
    }
});

await Run("Shutdown during startup also releases the message loop", async () =>
{
    for (var iteration = 0; iteration < 30; iteration++)
    {
        var monitor = Monitor();
        var starting = monitor.StartAsync(CancellationToken.None);
        var stopping = monitor.DisposeAsync().AsTask();
        await Task.WhenAll(starting, stopping).WaitAsync(TimeSpan.FromSeconds(5));
    }
});

await Run("Owned SSH-style process exits cleanly on standard-input EOF", async () =>
{
    using var child = StartChild("--child-eof");
    try
    {
        await SessionController.EndOwnedProcessAsync(child, closeInput: true).WaitAsync(TimeSpan.FromSeconds(8));
        Require(child.HasExited && child.ExitCode == 0, "EOF did not release the owned process cleanly.");
        await SessionController.EndOwnedProcessAsync(child, closeInput: true);
    }
    finally { await StopChild(child); }
});

await Run("A viewer that ignores graceful close is terminated within the deadline", async () =>
{
    using var child = StartChild("--child-wait");
    try
    {
        await SessionController.EndOwnedProcessAsync(child, closeInput: false).WaitAsync(TimeSpan.FromSeconds(8));
        Require(child.HasExited, "The unresponsive owned viewer survived cleanup.");
    }
    finally { await StopChild(child); }
});

Console.WriteLine($"{passed} passed; {failed} failed. No network connections or remote input were used.");
Console.WriteLine("Clipboard listeners were registered; tests did not publish or retain clipboard contents.");
return failed == 0 ? 0 : 1;

static WindowsClipboardMonitor Monitor() => new(_ => { }, _ => { });

static Process StartChild(string mode)
{
    var executable = Environment.ProcessPath ?? throw new IOException("Test executable is unavailable.");
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
    info.ArgumentList.Add(mode);
    return Process.Start(info) ?? throw new IOException("Could not start the owned test process.");
}

static async Task StopChild(Process child)
{
    if (!child.HasExited) child.Kill(entireProcessTree: false);
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

async Task Run(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
