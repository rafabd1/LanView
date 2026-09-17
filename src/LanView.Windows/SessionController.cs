using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using LanView.Windows.ClipboardIntegration;

namespace LanView.Windows;

// Control and streaming remain in OpenSSH, Sunshine and Moonlight.
// Only a fixed lifecycle command is sent to the installed host helper.
public sealed class SessionController : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? lease;
    private Process? viewer;
    private Profile? activeProfile;
    private CancellationTokenSource? lifetime;
    private Task? heartbeat;
    private Task? videoObservation;
    private bool disposed;
    private ClipboardBridge? clipboard;
    private ViewerWindowTracker? viewerWindow;

    public event Action<string>? Log;
    public event Action<SessionStatus>? StatusChanged;
    public event Action? SessionEnded;
    public bool IsSessionOpen => lease is not null && viewer is not null;
    public void CancelPendingConnection()
    {
        if (viewer is null) lifetime?.Cancel();
    }

    public async Task<SessionStatus> InspectAsync(Profile profile)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(profile, false);
        var result = await ReadStatusAsync(profile);
        StatusChanged?.Invoke(result);
        return result;
    }

    public async Task ConnectAsync(Profile profile)
    {
        await gate.WaitAsync();
        var hadOwnedSession = lease is not null || viewer is not null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Validate(profile, true);
            if (hadOwnedSession)
                throw new InvalidOperationException("Uma sessão já está aberta.");
            EnsureViewerNotRunning(profile);
            var hostStatus = await StartHostAsync(profile);
            viewer = StartViewer(profile, pairing: false);
            var ownedViewer = viewer;
            var ownedLease = lease;
            _ = WatchViewerAsync(ownedViewer, ownedLease!);
            videoObservation = ObserveVideoAsync(profile, DateTime.UtcNow.AddSeconds(-3), lifetime!.Token);
            if (profile.ShareClipboard) await StartClipboardAsync(profile);
            StatusChanged?.Invoke(new("Conectando", hostStatus.GpuState,
                "Moonlight iniciado; o vídeo ainda não foi confirmado. GPU conforme a última leitura do host."));
        }
        catch
        {
            if (!hadOwnedSession) await StopOwnedSessionAsync();
            throw;
        }
        finally { gate.Release(); }
    }

    public void OpenMoonlight(Profile profile)
    {
        // The UI's existing void entry point reports async failures through its log.
        _ = PairAsync(profile);
    }

    private async Task StartClipboardAsync(Profile profile)
    {
        clipboard = new ClipboardBridge();
        clipboard.Status += message => Log?.Invoke(message);
        try { await clipboard.StartAsync(profile); }
        catch (Exception)
        {
            Log?.Invoke("Não foi possível iniciar a sincronização. O vídeo continua disponível; confira o helper de clipboard no Linux.");
            await clipboard.StopAsync();
            clipboard = null;
        }
    }

    public async Task SetClipboardSharingAsync(bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            if (!enabled && clipboard is not null)
            {
                await clipboard.StopAsync();
                clipboard = null;
            }
            else if (enabled && clipboard is null && IsSessionOpen && activeProfile is not null)
                await StartClipboardAsync(activeProfile);
        }
        finally { gate.Release(); }
    }

    public async Task PairAsync(Profile profile)
    {
        await gate.WaitAsync();
        var hadOwnedSession = lease is not null || viewer is not null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Validate(profile, true);
            if (hadOwnedSession)
                throw new InvalidOperationException("Encerre a sessão antes de parear.");
            EnsureViewerNotRunning(profile);
            var hostStatus = await StartHostAsync(profile);
            viewer = StartViewer(profile, pairing: true);
            _ = WatchViewerAsync(viewer, lease!);
            StatusChanged?.Invoke(new("Pareamento", hostStatus.GpuState,
                "Confirme o PIN na interface do Sunshine. GPU conforme a última leitura do host."));
        }
        catch (Exception ex)
        {
            if (!hadOwnedSession) await StopOwnedSessionAsync();
            Log?.Invoke(ex.Message);
            if (!hadOwnedSession) StatusChanged?.Invoke(new("Erro", "desconhecida", ex.Message));
        }
        finally { gate.Release(); }
    }

    private async Task<SessionStatus> StartHostAsync(Profile profile)
    {
        Log?.Invoke("Iniciando o host pela conexão SSH já autorizada…");
        StatusChanged?.Invoke(new("Iniciando", "não consultada", "Preparando o host sob demanda."));
        var process = new Process { StartInfo = SshInfo(profile, "run", redirectInput: true) };
        try
        {
            if (!process.Start()) throw new IOException("Não foi possível iniciar o SSH.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        lease = process;
        activeProfile = profile;
        lifetime = new CancellationTokenSource();
        process.StandardInput.NewLine = "\n";
        heartbeat = KeepLeaseAsync(process, lifetime.Token);
        _ = DrainAsync(process.StandardError, lifetime.Token);
        var firstLine = await process.StandardOutput.ReadLineAsync(lifetime.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        if (firstLine is null || !TryParseStatus(firstLine, out var initial))
            throw new IOException("O host não respondeu. Verifique a instalação do helper e o journal do Sunshine.");
        if (initial!.State == "Parado")
            throw new IOException("O host não conseguiu iniciar. Verifique a captura e o encoder no Linux.");
        _ = DrainAsync(process.StandardOutput, lifetime.Token);
        Log?.Invoke("Aguardando a captura. Confirme o seletor de tela no Linux, se ele aparecer.");
        await WaitForSunshineAsync(profile, process, lifetime.Token);
        _ = WatchLeaseAsync(process);
        Log?.Invoke("Sunshine disponível. Vídeo e pareamento usam os clientes oficiais.");
        return initial;
    }

    private static async Task WaitForSunshineAsync(Profile profile, Process process, CancellationToken cancellation)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            cancellation.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("O host encerrou durante a inicialização.");
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(profile.Host, 47989, cancellation).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(1), cancellation);
                return;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                await Task.Delay(300, cancellation);
            }
        }
        throw new TimeoutException("Sunshine não ficou acessível na LAN. Confirme a seleção de tela no Linux, se houver, e verifique o log do host e o firewall.");
    }

    private async Task KeepLeaseAsync(Process process, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && !process.HasExited)
            {
                await process.StandardInput.WriteLineAsync();
                await process.StandardInput.FlushAsync(cancellation);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { }
    }

    private async Task ObserveVideoAsync(Profile profile, DateTime startedAfter, CancellationToken cancellation)
    {
        // Read only this viewer's bounded local diagnostics. Never forward raw protocol logs.
        try
        {
            for (var attempt = 0; attempt < 90; attempt++)
            {
                await Task.Delay(1000, cancellation);
                var log = new DirectoryInfo(ViewerState.DirectoryPath).EnumerateFiles("Moonlight-*.log")
                    .Where(file => file.CreationTimeUtc >= startedAfter)
                    .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
                if (log is null) continue;
                using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(Math.Max(0, stream.Length - 128 * 1024), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var tail = await reader.ReadToEndAsync(cancellation);
                if (!tail.Contains("Received first video packet after ", StringComparison.Ordinal)) continue;
                var status = await ReadStatusAsync(profile);
                cancellation.ThrowIfCancellationRequested();
                StatusChanged?.Invoke(new("Recebendo vídeo", status.GpuState,
                    "O visualizador recebeu vídeo. Mouse e teclado seguem o foco da janela."));
                return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private async Task DrainAsync(StreamReader reader, CancellationToken cancellation)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellation) is { } line)
                if (!string.IsNullOrWhiteSpace(line)) Log?.Invoke(line);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private Process StartViewer(Profile profile, bool pairing)
    {
        var info = ViewerStartInfo(profile, pairing, ViewerState.Prepare());
        var process = Process.Start(info) ?? throw new IOException("Não foi possível abrir o Moonlight.");
        if (!pairing)
        {
            try { viewerWindow = new ViewerWindowTracker(process.Id, message => Log?.Invoke(message)); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                Log?.Invoke("Não foi possível acompanhar o tamanho da janela. O vídeo continua disponível.");
            }
        }
        return process;
    }

    public static ProcessStartInfo ViewerStartInfo(Profile profile, bool pairing, string workingDirectory)
    {
        var info = new ProcessStartInfo(profile.MoonlightPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in ViewerArguments(profile, pairing)) info.ArgumentList.Add(argument);
        if (!pairing)
        {
            // SDL's fullscreen Alt+Tab escape keeps other system shortcuts captured.
            // A child-only environment hint overrides Moonlight's normal-priority hint.
            info.Environment["SDL_ALLOW_ALT_TAB_WHILE_GRABBED"] = "1";
        }
        return info;
    }

    public static IReadOnlyList<string> ViewerArguments(Profile profile, bool pairing)
    {
        if (pairing) return ["pair", profile.Host];
        return ["stream", profile.Host, "Desktop", "--1080", "--fps", "60",
            "--bitrate", "40000", "--display-mode", "windowed", "--absolute-mouse",
            "--video-codec", "HEVC", "--video-decoder", "hardware", "--yuv444",
            "--no-hdr", "--no-game-optimization", "--no-frame-pacing",
            "--capture-system-keys", "always"];
    }

    private static void EnsureViewerNotRunning(Profile profile)
    {
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(profile.MoonlightPath)))
        {
            using (candidate)
            {
                try
                {
                    if (string.Equals(candidate.MainModule?.FileName, profile.MoonlightPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Feche o Moonlight que já está aberto antes de iniciar uma sessão pelo LanView.");
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private async Task WatchViewerAsync(Process ownedViewer, Process ownedLease)
    {
        try { await ownedViewer.WaitForExitAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
        await gate.WaitAsync();
        try
        {
            if (ReferenceEquals(lease, ownedLease) || ReferenceEquals(viewer, ownedViewer))
                await StopOwnedSessionAsync();
        }
        catch (Exception ex) { Log?.Invoke("Falha ao liberar a sessão após fechar o Moonlight: " + ex.Message); }
        finally { gate.Release(); }
    }

    private async Task WatchLeaseAsync(Process ownedLease)
    {
        try { await ownedLease.WaitForExitAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
        await gate.WaitAsync();
        try
        {
            if (ReferenceEquals(lease, ownedLease))
            {
                Log?.Invoke("A sessão SSH terminou; encerrando o visualizador desta sessão.");
                await StopOwnedSessionAsync();
            }
        }
        catch (Exception ex) { Log?.Invoke("Falha ao liberar a sessão após encerrar o SSH: " + ex.Message); }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { await StopOwnedSessionAsync(); }
        finally { gate.Release(); }
    }

    private async Task StopOwnedSessionAsync()
    {
        var ownedLease = lease;
        var ownedViewer = viewer;
        var previousProfile = activeProfile;
        var failures = new List<Exception>();
        if (clipboard is not null)
        {
            try { await clipboard.StopAsync(); }
            catch (Exception) { Log?.Invoke("A sincronização encerrou com erro. O host de vídeo será liberado."); }
            clipboard = null;
        }
        lifetime?.Cancel();
        if (videoObservation is not null)
        {
            try { await videoObservation; } catch (Exception) { }
            videoObservation = null;
        }
        if (heartbeat is not null) await heartbeat;
        heartbeat = null;
        if (ownedViewer is not null)
        {
            try
            {
                viewerWindow?.Dispose();
                viewerWindow = null;
                await EndOwnedProcessAsync(ownedViewer, closeInput: false);
                ownedViewer.Dispose();
                viewer = null;
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (ownedLease is not null)
        {
            // EOF releases the Linux lease. If the network is gone, its timeout still applies.
            try
            {
                await EndOwnedProcessAsync(ownedLease, closeInput: true);
                ownedLease.Dispose();
                lease = null;
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        lifetime?.Dispose();
        lifetime = null;
        if (failures.Count > 0)
        {
            // Keep failed process handles so a later Disconnect can retry.
            StatusChanged?.Invoke(new("Encerramento incompleto", "não consultada",
                "Não foi possível encerrar todos os processos locais. Use Desconectar para tentar novamente."));
            throw new AggregateException("Falha ao encerrar os processos da sessão.", failures);
        }
        activeProfile = null;
        if (ownedLease is not null || ownedViewer is not null) SessionEnded?.Invoke();
        StatusChanged?.Invoke(new("Sessão local encerrada", "não consultada",
            "Processos locais encerrados. O estado do host e da GPU depende da consulta ao Linux."));
        if (previousProfile is not null)
        {
            try { StatusChanged?.Invoke(await ReadStatusAsync(previousProfile)); }
            catch (Exception ex) { Log?.Invoke("Sessão encerrada; não foi possível consultar o estado final: " + ex.Message); }
        }
    }

    private static async Task EndOwnedProcessAsync(Process process, bool closeInput)
    {
        if (process.HasExited) return;
        if (closeInput)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
        else process.CloseMainWindow();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(closeInput ? 12 : 4)); }
        catch (TimeoutException)
        {
            // This Process object belongs to this session; never terminate by process name.
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync();
        }
    }

    private static ProcessStartInfo SshInfo(Profile profile, string action, bool redirectInput = false)
    {
        if (action is not ("status" or "run")) throw new ArgumentOutOfRangeException(nameof(action));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2",
            profile.User + "@" + profile.Host, "~/.local/bin/lanview-host", action }) info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<SessionStatus> ReadStatusAsync(Profile profile)
    {
        using var process = Process.Start(SshInfo(profile, "status")) ?? throw new IOException("SSH indisponível.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("A consulta SSH excedeu o tempo limite.");
        }
        if (process.ExitCode != 0) throw new IOException((await error).Trim());
        var json = (await output).Trim();
        if (!TryParseStatus(json, out var status)) throw new IOException("Resposta de status inválida do host.");
        return status!;
    }

    public static bool TryParseStatus(string json, out SessionStatus? status)
    {
        status = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("running", out var running) || running.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            if (!root.TryGetProperty("gpuRuntimeStatus", out var gpu) || gpu.ValueKind != JsonValueKind.String) return false;
            var gpuState = gpu.GetString() switch
            {
                "active" => "ativa", "suspended" => "suspensa", "suspending" => "suspendendo",
                "resuming" => "ativando", _ => "desconhecida"
            };
            status = new(running.GetBoolean() ? "Host ativo" : "Parado", gpuState,
                running.GetBoolean() ? "Host em execução; este status não confirma que há vídeo." : "Host parado. Consultar este estado não acorda a GPU.");
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void Validate(Profile profile, bool requireMoonlight)
    {
        if (profile.Validate(requireMoonlight) is { } error) throw new ArgumentException(error);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime?.Cancel();
        viewerWindow?.Dispose();
        viewerWindow = null;
        // Normal UI shutdown awaits DisconnectAsync. Abnormal disposal breaks the lease too.
        try { lease?.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }
}
