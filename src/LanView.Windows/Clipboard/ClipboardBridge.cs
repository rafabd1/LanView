using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace LanView.Windows.ClipboardIntegration;

public sealed class ClipboardBridge : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private BridgeSession? session;
    private bool disposed;
    public event Action<string>? Status;

    public async Task StartAsync(Profile profile)
    {
        await lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is not null) throw new InvalidOperationException("A sincronização já está ativa.");
            if (profile.Validate(requireMoonlight: false) is { } error) throw new ArgumentException(error);
            var started = new BridgeSession(profile, message => Status?.Invoke(message));
            session = started;
            try { await started.StartAsync(); }
            catch { await started.StopAsync(); session = null; throw; }
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            var owned = session;
            session = null;
            if (owned is not null) await owned.StopAsync();
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() { disposed = true; await StopAsync(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session?.Abort();
        _ = StopAsync();
    }

    public static ProcessStartInfo CreateSshStartInfo(Profile profile)
    {
        if (profile.Validate(requireMoonlight: false) is { } error) throw new ArgumentException(error);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2",
            profile.User + "@" + profile.Host, "~/.local/bin/lanview-bridge" })
            info.ArgumentList.Add(argument);
        return info;
    }

    private sealed record Work(long Revision, string Id, ClipboardSnapshot? Local, string? Text, IReadOnlyList<ClipboardFileEntry>? Files);

    private sealed class BridgeSession
    {
        private readonly Process process;
        private readonly Action<string> status;
        private readonly CancellationTokenSource stop = new();
        private readonly SemaphoreSlim writes = new(1, 1);
        private readonly Channel<Work> pending = Channel.CreateBounded<Work>(new BoundedChannelOptions(16)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        private readonly ConcurrentDictionary<string, TaskCompletionSource> acknowledgements = new();
        private readonly ConcurrentDictionary<string, byte> sentIds = new();
        private readonly ConcurrentQueue<string> recentIds = new();
        private readonly ConcurrentDictionary<string, byte> ignoredDownloads = new();
        private readonly ConcurrentQueue<string> ignoredDownloadOrder = new();
        private TaskCompletionSource selectionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WindowsClipboardMonitor monitor;
        private Task? reader;
        private Task? worker;
        private Task? stderr;
        private Task? heartbeat;
        private DownloadCache? download;
        private long revision;
        private bool running;

        public BridgeSession(Profile profile, Action<string> status)
        {
            process = new Process { StartInfo = CreateSshStartInfo(profile) };
            this.status = status;
            monitor = new WindowsClipboardMonitor(OnLocalChange, status);
        }

        public async Task StartAsync()
        {
            if (!process.Start()) throw new IOException("Não foi possível iniciar a sincronização SSH.");
            running = true;
            process.StandardInput.NewLine = "\n";
            stderr = DiscardErrorsAsync();
            reader = ReadAsync();
            await SendAsync(new { type = "hello", version = 1 });
            await hello.Task.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            heartbeat = KeepAliveAsync();
            await monitor.StartAsync(stop.Token);
            worker = WorkAsync();
            status("Texto e arquivos sincronizados durante esta sessão.");
        }

        private void OnLocalChange(ClipboardSnapshot snapshot)
        {
            if (stop.IsCancellationRequested) return;
            if (snapshot.Text is null && snapshot.Files is null)
            {
                Interlocked.Increment(ref revision);
                Interlocked.Exchange(ref selectionChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                return;
            }
            var id = Guid.NewGuid().ToString("N");
            Queue(new(0, id, snapshot, null, null));
        }

        private void Queue(Work work)
        {
            var latest = Interlocked.Increment(ref revision);
            var previous = Interlocked.Exchange(ref selectionChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            pending.Writer.TryWrite(work with { Revision = latest });
            previous.TrySetResult();
        }

        private void RememberId(string id)
        {
            sentIds.TryAdd(id, 0);
            recentIds.Enqueue(id);
            while (recentIds.Count > 64 && recentIds.TryDequeue(out var oldest)) sentIds.TryRemove(oldest, out _);
        }

        private async Task WorkAsync()
        {
            try
            {
                await foreach (var work in pending.Reader.ReadAllAsync(stop.Token))
                {
                    if (work.Revision != Interlocked.Read(ref revision)) continue;
                    try
                    {
                        if (work.Local is { } local)
                        {
                            RememberId(work.Id);
                            if (local.Files is { } files) await UploadAsync(work.Id, files, work.Revision);
                            else await SendAcknowledgedAsync(work.Id, new { type = "set-text", id = work.Id, text = local.Text ?? "" });
                        }
                        else if (work.Files is not null) await DownloadAsync(work);
                        else
                        {
                            var text = work.Text ?? "";
                            await monitor.PublishAsync(new(text, null, ClipboardFilePolicy.FingerprintText(text)), stop.Token);
                        }
                    }
                    catch (InvalidDataException ex) { status(ex.Message); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or TimeoutException or System.Runtime.InteropServices.ExternalException)
                    {
                        status("Não foi possível sincronizar esta seleção. Copie novamente para tentar.");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Fail(); }
        }

        private async Task UploadAsync(string id, string[] paths, long initialRevision)
        {
            var selection = await Task.Run(() => ClipboardFilePolicy.EnumerateSelection(paths, stop.Token), stop.Token);
            if (initialRevision != Interlocked.Read(ref revision)) return;
            await SendAsync(new { type = "upload-begin", id, entries = selection.Select(file => file.Entry) });
            status("Copiando arquivos para o Linux…");
            var buffer = new byte[ClipboardFilePolicy.MaxChunkBytes];
            var pace = Stopwatch.StartNew();
            long sent = 0;
            foreach (var file in selection.Where(file => !file.Entry.Directory))
            {
                ClipboardFilePolicy.EnsureNoReparsePoints(file.Source);
                await using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    ClipboardFilePolicy.MaxChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != file.Entry.Size) throw new IOException("A seleção mudou durante a cópia.");
                long offset = 0;
                while (offset < file.Entry.Size)
                {
                    if (initialRevision != Interlocked.Read(ref revision))
                    {
                        await SendAcknowledgedAsync(id, new { type = "upload-cancel", id });
                        return;
                    }
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Entry.Size - offset)), stop.Token);
                    if (count == 0) throw new EndOfStreamException();
                    await SendAsync(new { type = "upload-chunk", id, path = file.Entry.Path, offset, data = Convert.ToBase64String(buffer, 0, count) });
                    offset += count;
                    sent += count;
                    var wait = TimeSpan.FromSeconds(sent / (8d * 1024 * 1024)) - pace.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);
                }
                if (input.Length != file.Entry.Size) throw new IOException("A seleção mudou durante a cópia.");
            }
            if (initialRevision != Interlocked.Read(ref revision))
            {
                await SendAcknowledgedAsync(id, new { type = "upload-cancel", id });
                return;
            }
            await SendAcknowledgedAsync(id, new { type = "upload-commit", id });
            status("Arquivos prontos para colar no Linux.");
        }

        private async Task DownloadAsync(Work work)
        {
            var superseded = Volatile.Read(ref selectionChanged).Task;
            if (work.Revision != Interlocked.Read(ref revision)) return;
            using var cache = new DownloadCache(work.Id, work.Files!);
            Volatile.Write(ref download, cache);
            try
            {
                status("Recebendo arquivos do Linux…");
                await SendAsync(new { type = "download", id = work.Id });
                var result = await Task.WhenAny(cache.Complete.Task, superseded).WaitAsync(TimeSpan.FromMinutes(12), stop.Token);
                if (result == superseded)
                {
                    ignoredDownloads.TryAdd(work.Id, 0);
                    ignoredDownloadOrder.Enqueue(work.Id);
                    while (ignoredDownloadOrder.Count > 64 && ignoredDownloadOrder.TryDequeue(out var old)) ignoredDownloads.TryRemove(old, out _);
                    try { await SendAcknowledgedAsync(work.Id, new { type = "download-cancel", id = work.Id }); }
                    // The host may already have completed or invalidated this offer.
                    catch (IOException) { }
                    return;
                }
                await cache.Complete.Task;
                // A later local copy or remote offer always wins over a slow download.
                if (work.Revision != Interlocked.Read(ref revision)) return;
                var paths = cache.Commit();
                if (await monitor.PublishAsync(new(null, paths, ClipboardFilePolicy.FingerprintPaths(paths)), stop.Token))
                    status("Arquivos prontos para colar no Windows.");
            }
            finally { Volatile.Write(ref download, null); }
        }

        private async Task SendAcknowledgedAsync(string id, object message)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!acknowledgements.TryAdd(id, completion)) throw new IOException("Transferência já em andamento.");
            try
            {
                await SendAsync(message);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            }
            finally { acknowledgements.TryRemove(id, out _); }
        }

        private async Task SendAsync(object message)
        {
            await writes.WaitAsync(stop.Token);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), stop.Token);
                await process.StandardInput.FlushAsync(stop.Token);
            }
            finally { writes.Release(); }
        }

        private async Task ReadAsync()
        {
            try
            {
                await foreach (var line in ReadProtocolLinesAsync(process.StandardOutput, stop.Token))
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = root.GetProperty("type").GetString();
                    if (type == "hello")
                    {
                        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
                        hello.TrySetResult();
                        continue;
                    }
                    if (!hello.Task.IsCompletedSuccessfully) throw new InvalidDataException();
                    var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
                    if (type == "error")
                    {
                        var failure = new IOException("O host não concluiu a transferência.");
                        if (acknowledgements.TryRemove(id, out var ack)) ack.TrySetException(failure);
                        if (Volatile.Read(ref download) is { } active && active.Id == id) active.Complete.TrySetException(failure);
                        if (!ignoredDownloads.ContainsKey(id)) status("O host não conseguiu sincronizar esta seleção.");
                        continue;
                    }
                    if (!ClipboardFilePolicy.IsValidId(id)) throw new InvalidDataException();
                    switch (type)
                    {
                        case "ack":
                            if (acknowledgements.TryRemove(id, out var ack)) ack.TrySetResult();
                            break;
                        case "offer":
                            if (sentIds.ContainsKey(id)) break;
                            var kind = root.GetProperty("kind").GetString();
                            if (kind == "text")
                            {
                                var text = root.GetProperty("text").GetString() ?? throw new InvalidDataException();
                                if (Encoding.UTF8.GetByteCount(text) > ClipboardFilePolicy.MaxTextBytes) throw new InvalidDataException();
                                Queue(new(0, id, null, text, null));
                            }
                            else if (kind == "files")
                            {
                                var entries = root.GetProperty("entries").Deserialize<ClipboardFileEntry[]>() ?? throw new InvalidDataException();
                                var valid = ClipboardFilePolicy.ValidateManifest(entries);
                                Queue(new(0, id, null, null, valid));
                            }
                            else throw new InvalidDataException();
                            break;
                        case "file-chunk":
                            {
                                if (ignoredDownloads.ContainsKey(id)) break;
                                var active = Volatile.Read(ref download);
                                if (active is null || active.Id != id) throw new InvalidDataException();
                                active.WriteChunk(root.GetProperty("path").GetString() ?? "", root.GetProperty("offset").GetInt64(), root.GetProperty("data").GetString() ?? "");
                                break;
                            }
                        case "download-complete":
                            {
                                if (ignoredDownloads.ContainsKey(id)) break;
                                var active = Volatile.Read(ref download);
                                if (active is null || active.Id != id) throw new InvalidDataException();
                                active.ValidateComplete();
                                active.Complete.TrySetResult();
                                break;
                            }
                        default: throw new InvalidDataException();
                    }
                }
                if (!stop.IsCancellationRequested) Fail();
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!stop.IsCancellationRequested) Fail(); }
        }

        private void Fail()
        {
            if (stop.IsCancellationRequested) return;
            hello.TrySetException(new IOException("O helper de sincronização não está disponível no Linux."));
            Abort();
            _ = StopMonitorAfterFailureAsync();
            status("Sincronização interrompida; reconecte para retomá-la.");
        }

        private async Task StopMonitorAfterFailureAsync()
        {
            try { await monitor.DisposeAsync(); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException) { }
        }

        private async Task DiscardErrorsAsync()
        {
            var buffer = new char[4096];
            try { while (await process.StandardError.ReadAsync(buffer, stop.Token) != 0) { } }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }

        private async Task KeepAliveAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                    await SendAsync(new { type = "ping" });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!stop.IsCancellationRequested) Fail(); }
        }

        public void Abort()
        {
            stop.Cancel();
            try { if (running) process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }

        public async Task StopAsync()
        {
            Abort();
            pending.Writer.TryComplete();
            try { if (running) process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            try { await monitor.DisposeAsync(); } catch (TimeoutException) { }
            foreach (var task in new[] { worker, reader, stderr, heartbeat })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            }
            if (running)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync();
                }
            }
            process.Dispose();
            status("Sincronização encerrada.");
        }
    }

    public static async IAsyncEnumerable<string> ReadProtocolLinesAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        const int maxLineCharacters = 8 * 1024 * 1024;
        var buffer = new char[8192];
        var current = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != '\n') continue;
                if (current.Length + index - start > maxLineCharacters) throw new InvalidDataException("Mensagem de sincronização muito grande.");
                current.Append(buffer, start, index - start);
                if (current.Length > 0 && current[^1] == '\r') current.Length--;
                yield return current.ToString();
                current.Clear();
                start = index + 1;
            }
            if (current.Length + count - start > maxLineCharacters) throw new InvalidDataException("Mensagem de sincronização muito grande.");
            current.Append(buffer, start, count - start);
        }
        if (current.Length != 0) throw new InvalidDataException("Mensagem de sincronização incompleta.");
    }
}

public sealed class DownloadCache : IDisposable
{
    private readonly object access = new();
    private readonly IReadOnlyList<ClipboardFileEntry> entries;
    private readonly Dictionary<string, long> expected;
    private readonly Dictionary<string, long> received;
    private readonly string root;
    private bool committed;
    private bool disposed;
    public string Id { get; }
    public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadCache(string id, IReadOnlyList<ClipboardFileEntry> entries, string? cacheDirectory = null)
    {
        if (!ClipboardFilePolicy.IsValidId(id)) throw new InvalidDataException("Identificador inválido.");
        Id = id;
        this.entries = ClipboardFilePolicy.ValidateManifest(entries);
        var directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "LanView", "Clipboard");
        Directory.CreateDirectory(directory);
        ClipboardFilePolicy.EnsureNoReparsePoints(directory);
        root = Path.Combine(directory, "transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        expected = this.entries.Where(entry => !entry.Directory).ToDictionary(entry => entry.Path, entry => entry.Size, StringComparer.Ordinal);
        received = expected.Keys.ToDictionary(path => path, _ => 0L, StringComparer.Ordinal);
        try
        {
            foreach (var entry in this.entries.OrderBy(entry => entry.Path.Count(c => c == '/')))
            {
                var full = ClipboardFilePolicy.ResolveUnderRoot(root, entry.Path);
                if (entry.Directory) Directory.CreateDirectory(full);
                else using (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            }
        }
        catch { RemoveStaging(); throw; }
    }

    public void WriteChunk(string relative, long offset, string base64)
    {
        lock (access)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (committed || !expected.TryGetValue(relative, out var size) || offset != received[relative]
                || base64.Length > ((ClipboardFilePolicy.MaxChunkBytes + 2) / 3) * 4)
                throw new InvalidDataException("Bloco de arquivo inválido.");
            var data = Convert.FromBase64String(base64);
            if (data.Length is 0 or > ClipboardFilePolicy.MaxChunkBytes || data.LongLength > size - offset)
                throw new InvalidDataException("Tamanho do bloco inválido.");
            var path = ClipboardFilePolicy.ResolveUnderRoot(root, relative);
            ClipboardFilePolicy.EnsureNoReparsePoints(path);
            using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            if (output.Length != offset) throw new InvalidDataException("Arquivo alterado durante a transferência.");
            output.Position = offset;
            output.Write(data);
            received[relative] += data.Length;
        }
    }

    public void ValidateComplete()
    {
        lock (access)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (expected.Any(item => received[item.Key] != item.Value)) throw new InvalidDataException("Transferência incompleta.");
            foreach (var entry in entries)
            {
                var path = ClipboardFilePolicy.ResolveUnderRoot(root, entry.Path);
                ClipboardFilePolicy.EnsureNoReparsePoints(path);
                if (!entry.Directory && new FileInfo(path).Length != entry.Size) throw new InvalidDataException("Transferência incompleta.");
            }
        }
    }

    public string[] Commit()
    {
        lock (access)
        {
            ValidateComplete();
            committed = true;
            // Retain completed files after disconnect so the ordinary Windows clipboard stays usable.
            return entries.Where(entry => !entry.Path.Contains('/')).Select(entry => ClipboardFilePolicy.ResolveUnderRoot(root, entry.Path)).ToArray();
        }
    }

    private void RemoveStaging()
    {
        if (!Directory.Exists(root)) return;
        // Remove only paths from the validated manifest; never recursively follow a reparse point.
        foreach (var entry in entries.OrderByDescending(entry => entry.Path.Count(c => c == '/')))
        {
            var path = ClipboardFilePolicy.ResolveUnderRoot(root, entry.Path);
            try
            {
                ClipboardFilePolicy.EnsureNoReparsePoints(path);
                if (entry.Directory) Directory.Delete(path, recursive: false);
                else File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { ClipboardFilePolicy.EnsureNoReparsePoints(root); Directory.Delete(root, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (access)
        {
            if (disposed) return;
            disposed = true;
            if (!committed) RemoveStaging();
        }
    }
}
