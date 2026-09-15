using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;

namespace LanView.Windows.ClipboardIntegration;

internal sealed record ClipboardSnapshot(string? Text, string[]? Files, string Fingerprint);

internal sealed class WindowsClipboardMonitor : IAsyncDisposable
{
    private readonly TaskCompletionSource<ClipboardWindow> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<ClipboardSnapshot> changed;
    private readonly Action<string> status;
    private ApplicationContext? context;
    private readonly Thread thread;

    public WindowsClipboardMonitor(Action<ClipboardSnapshot> changed, Action<string> status)
    {
        this.changed = changed;
        this.status = status;
        thread = new Thread(Run) { IsBackground = true, Name = "LanView clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
    }

    public async Task StartAsync(CancellationToken cancellation)
    {
        thread.Start();
        await ready.Task.WaitAsync(cancellation);
    }

    private void Run()
    {
        try
        {
            using var window = new ClipboardWindow(changed, status);
            using var applicationContext = new ApplicationContext();
            context = applicationContext;
            _ = window.Handle;
            ready.TrySetResult(window);
            Application.Run(applicationContext);
        }
        catch (Exception) { ready.TrySetException(new IOException("Não foi possível acompanhar a área de transferência.")); }
        finally { finished.TrySetResult(); }
    }

    public async Task<bool> PublishAsync(ClipboardSnapshot snapshot, CancellationToken cancellation)
    {
        var window = await ready.Task.WaitAsync(cancellation);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await window.InvokeAsync(() => window.Publish(snapshot), cancellation);
            }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(80, cancellation); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!thread.IsAlive) return;
        try
        {
            if (ready.Task.IsCompletedSuccessfully)
                await ready.Task.Result.InvokeAsync(() => context?.ExitThread());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class ClipboardWindow : Form
    {
        private const int WmClipboardUpdate = 0x031D;
        private readonly Action<ClipboardSnapshot> changed;
        private readonly Action<string> status;
        private readonly System.Windows.Forms.Timer retry = new() { Interval = 80 };
        private uint observedSequence;
        private string? publishedFingerprint;
        private int attempts;
        private long localChanges;

        public ClipboardWindow(Action<ClipboardSnapshot> changed, Action<string> status)
        {
            this.changed = changed;
            this.status = status;
            ShowInTaskbar = false;
            retry.Tick += (_, _) => CaptureSelection();
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            observedSequence = GetClipboardSequenceNumber();
            if (!AddClipboardFormatListener(Handle)) throw new IOException("Área de transferência indisponível.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmClipboardUpdate)
            {
                attempts = 0;
                retry.Start();
            }
            base.WndProc(ref m);
        }

        private void CaptureSelection()
        {
            retry.Stop();
            try
            {
                var sequence = GetClipboardSequenceNumber();
                if (sequence == observedSequence) return;
                ClipboardSnapshot? snapshot = null;
                if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                {
                    var paths = System.Windows.Forms.Clipboard.GetFileDropList().Cast<string>().ToArray();
                    if (paths.Length > ClipboardFilePolicy.MaxEntries)
                    {
                        observedSequence = sequence;
                        NotifyUnsupported();
                        status("A seleção ultrapassa 4096 arquivos e pastas.");
                        return;
                    }
                    if (paths.Length > 0) snapshot = new(null, paths, ClipboardFilePolicy.FingerprintPaths(paths));
                }
                else if (System.Windows.Forms.Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    var text = System.Windows.Forms.Clipboard.GetText(TextDataFormat.UnicodeText);
                    if (Encoding.UTF8.GetByteCount(text) > ClipboardFilePolicy.MaxTextBytes)
                    {
                        observedSequence = sequence;
                        NotifyUnsupported();
                        status("O texto ultrapassa o limite de 1 MiB.");
                        return;
                    }
                    snapshot = new(text, null, ClipboardFilePolicy.FingerprintText(text));
                }
                // If the clipboard changed during extraction, retry the newest selection.
                if (sequence != GetClipboardSequenceNumber()) { retry.Start(); return; }
                observedSequence = sequence;
                if (snapshot is null) { NotifyUnsupported(); return; }
                if (snapshot.Fingerprint == publishedFingerprint) return;
                publishedFingerprint = null;
                localChanges++;
                changed(snapshot);
            }
            catch (ExternalException)
            {
                if (++attempts < 5) retry.Start();
                else status("A área de transferência está ocupada; copie novamente.");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Security.SecurityException)
            { status("Não foi possível ler esta seleção da área de transferência."); }
        }

        private void NotifyUnsupported()
        {
            publishedFingerprint = null;
            localChanges++;
            changed(new(null, null, "unsupported"));
        }

        public bool Publish(ClipboardSnapshot snapshot)
        {
            // A clipboard event can be waiting on the retry timer when a download finishes.
            // Read that change first, so an incoming offer cannot overwrite the user's newer copy.
            if (GetClipboardSequenceNumber() != observedSequence)
            {
                var before = localChanges;
                CaptureSelection();
                if (localChanges != before) return false;
                if (GetClipboardSequenceNumber() != observedSequence) throw new ExternalException("Área de transferência ocupada.");
            }
            var oldFingerprint = publishedFingerprint;
            publishedFingerprint = snapshot.Fingerprint;
            try
            {
                if (snapshot.Files is { } files)
                {
                    var list = new StringCollection();
                    list.AddRange(files);
                    System.Windows.Forms.Clipboard.SetFileDropList(list);
                }
                else
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, snapshot.Text ?? "");
                    System.Windows.Forms.Clipboard.SetDataObject(data, true);
                }
                observedSequence = GetClipboardSequenceNumber();
                return true;
            }
            catch { publishedFingerprint = oldFingerprint; throw; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                retry.Dispose();
                if (IsHandleCreated) RemoveClipboardFormatListener(Handle);
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
