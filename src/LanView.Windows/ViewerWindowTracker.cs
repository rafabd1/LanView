using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LanView.Windows;

// Created and disposed on the UI thread. Out-of-context WinEvents use its message loop.
// Hooks are limited to the viewer process; no input hook, polling or focus change is needed.
internal sealed class ViewerWindowTracker : IDisposable
{
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const long CaptionStyle = 0x00C00000;
    private const uint KeepZOrder = 0x0004;
    private const uint DoNotActivate = 0x0010;
    private const uint KeepOwnerZOrder = 0x0200;
    private const uint AsyncWindowPosition = 0x4000;
    private readonly uint processId;
    private readonly Action<string> report;
    private readonly WinEventCallback callback;
    private readonly List<nint> hooks = [];
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 350 };
    private ViewerWindowSize? lastSize;
    private nint window;
    private bool applying;
    private bool disposed;
    private bool errorReported;

    public ViewerWindowTracker(int processId, Action<string> report)
    {
        this.processId = checked((uint)processId);
        this.report = report;
        callback = OnWindowEvent;
        lastSize = ViewerWindowSizeStore.Load();
        saveTimer.Tick += OnSaveTimer;
        try
        {
            foreach (var eventId in new[] { EventObjectShow, EventObjectLocationChange, EventSystemMoveSizeEnd })
            {
                var hook = SetWinEventHook(eventId, eventId, 0, callback, this.processId, 0, 0);
                if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                hooks.Add(hook);
            }
            // Covers a window that became visible before the hooks were installed.
            EnumWindows((candidate, _) => { ConsiderWindow(candidate); return window == 0; }, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OnWindowEvent(nint hook, uint eventId, nint candidate, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (disposed || applying || candidate == 0 || objectId != 0 || childId != 0) return;
        try
        {
            if (window == 0 && eventId is EventObjectShow or EventObjectLocationChange)
            {
                ConsiderWindow(candidate);
                return;
            }
            if (candidate != window) return;
            if (eventId == EventSystemMoveSizeEnd)
            {
                saveTimer.Stop();
                RememberSize();
            }
            else if (eventId == EventObjectLocationChange)
            {
                // Coalesce resize bursts and wait for fullscreen/maximize transitions to finish.
                saveTimer.Stop();
                saveTimer.Start();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ReportFailure();
        }
    }

    private bool IsViewerWindow(nint candidate)
    {
        GetWindowThreadProcessId(candidate, out var owner);
        if (owner != processId || GetAncestor(candidate, 2) != candidate || !IsWindowVisible(candidate)) return false;
        var className = new StringBuilder(64);
        var title = new StringBuilder(256);
        GetClassName(candidate, className, className.Capacity);
        GetWindowText(candidate, title, title.Capacity);
        // Decoder probes are hidden SDL windows without a title; the launcher uses Qt.
        return className.ToString() == "SDL_app" && title.ToString().EndsWith(" - Moonlight", StringComparison.Ordinal);
    }

    private bool IsNormalWindow() => window != 0 && IsViewerWindow(window)
        && !IsIconic(window) && !IsZoomed(window)
        && (GetWindowLongPtr(window, -16).ToInt64() & CaptionStyle) == CaptionStyle;

    private void ConsiderWindow(nint candidate)
    {
        if (window != 0 || !IsViewerWindow(candidate)) return;
        window = candidate;
        if (lastSize is null || !IsNormalWindow() || !GetWindowRect(window, out var nativeBounds)) return;
        var dpi = GetDpiForWindow(window);
        if (dpi is < 48 or > 768) return;
        var bounds = nativeBounds.ToRectangle();
        var restored = lastSize.FitToWorkingArea(bounds, Screen.FromHandle(window).WorkingArea, dpi);
        applying = true;
        try
        {
            // Keep the current Z order and focus, including when another app is active.
            // Do not block the launcher while the viewer initializes its decoder.
            if (!SetWindowPos(window, 0, restored.X, restored.Y, restored.Width, restored.Height,
                KeepZOrder | DoNotActivate | KeepOwnerZOrder | AsyncWindowPosition)) ReportFailure();
        }
        finally { applying = false; }
    }

    private void OnSaveTimer(object? sender, EventArgs e)
    {
        saveTimer.Stop();
        RememberSize();
    }

    private void RememberSize()
    {
        // SDL removes the caption in fullscreen; neither that nor maximize replaces normal size.
        if (!IsNormalWindow() || !GetWindowRect(window, out var bounds)) return;
        var size = new ViewerWindowSize(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, GetDpiForWindow(window));
        if (!size.IsValid || size == lastSize) return;
        try
        {
            ViewerWindowSizeStore.Save(size);
            lastSize = size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportFailure();
        }
    }

    private void ReportFailure()
    {
        if (errorReported) return;
        errorReported = true;
        report("Não foi possível guardar ou restaurar o tamanho da janela. O vídeo continua disponível.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        saveTimer.Stop();
        RememberSize();
        saveTimer.Dispose();
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear();
        GC.KeepAlive(callback);
    }

    private delegate void WinEventCallback(nint hook, uint eventId, nint window, int objectId,
        int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder title, int maxCount);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
