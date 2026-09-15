namespace LanView.Windows;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(20, 23, 29);
    private static readonly Color Surface = Color.FromArgb(30, 35, 43);
    private static readonly Color Border = Color.FromArgb(56, 64, 77);
    private static readonly Color Foreground = Color.FromArgb(236, 240, 246);
    private static readonly Color Muted = Color.FromArgb(157, 169, 186);
    private static readonly Color Accent = Color.FromArgb(104, 170, 255);

    private readonly SessionController _controller = new();
    private readonly TextBox _host = Input("Endereço IPv4 local");
    private readonly TextBox _user = Input("Usuário SSH");
    private string _viewerPath = "";
    private readonly Label _sessionState = StatusValue("Pronto");
    private readonly Label _gpuState = StatusValue("Não consultada");
    private readonly Label _detail = MakeLabel("Verifique o Linux para consultar o host e a GPU.", Muted);
    private readonly TextBox _log = new();
    private readonly Queue<string> _logLines = new();
    private readonly Button _save = ActionButton("Salvar perfil");
    private readonly Button _inspect = ActionButton("Verificar");
    private readonly Button _connect = ActionButton("Conectar", primary: true);
    private readonly Button _disconnect = ActionButton("Desconectar");
    private readonly Button _openMoonlight = ActionButton("Parear");
    private readonly ToolTip _tips = new();
    private readonly Icon _applicationIcon = AppIcon.Load();
    private readonly NotifyIcon _tray = new() { Text = "LanView" };
    private readonly CheckBox _shareClipboard = new()
    {
        Text = "Sincronizar texto e arquivos copiados durante a sessão",
        Checked = true, AutoSize = true, ForeColor = Foreground,
        Margin = new Padding(0, 0, 0, 14)
    };
    private bool _busy;
    private bool _closing;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _resourcesDisposed;

    public MainForm()
    {
        Text = "LanView";
        Icon = _applicationIcon;
        _tray.Icon = _applicationIcon;
        BackColor = Background;
        ForeColor = Foreground;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(740, 640);
        MinimumSize = new Size(710, 610);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(28, 24, 28, 22);

        BuildLayout();
        BindActions();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Abrir LanView", null, (_, _) => RestoreLauncher());
        trayMenu.Items.Add("Encerrar sessão", null, async (_, _) => await RunOperationAsync(_controller.DisconnectAsync));
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreLauncher();
        _controller.Log += OnControllerLog;
        _controller.StatusChanged += OnControllerStatus;
        _controller.SessionEnded += OnSessionEnded;
        LoadProfile();
        AcceptButton = _connect;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            BackColor = Background,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 20)
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = MakeLabel("LanView", Foreground);
        title.Font = new Font("Segoe UI", 25, FontStyle.Bold);
        title.Margin = Padding.Empty;
        heading.Controls.Add(title, 0, 0);
        var version = MakeLabel("LAN · 1080P · 60 FPS", Muted);
        version.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        version.Anchor = AnchorStyles.Right;
        heading.Controls.Add(version, 1, 0);
        var subtitle = MakeLabel("Seu Linux, em uma janela.", Muted);
        subtitle.Margin = new Padding(1, 4, 0, 0);
        heading.Controls.Add(subtitle, 0, 1);
        root.Controls.Add(heading, 0, 0);

        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            BackColor = Surface, Padding = new Padding(16, 14, 16, 14),
            Margin = new Padding(0, 0, 0, 20)
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        status.Controls.Add(MakeLabel("SESSÃO", Muted), 0, 0);
        status.Controls.Add(MakeLabel("GPU NO LINUX", Muted), 1, 0);
        status.Controls.Add(_sessionState, 0, 1);
        status.Controls.Add(_gpuState, 1, 1);
        _detail.Dock = DockStyle.Fill;
        _detail.Margin = new Padding(0, 12, 0, 0);
        status.Controls.Add(_detail, 0, 2);
        status.SetColumnSpan(_detail, 2);
        root.Controls.Add(status, 0, 1);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 14)
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        fields.Controls.Add(MakeLabel("Linux · endereço local", Muted), 0, 0);
        fields.Controls.Add(MakeLabel("Usuário SSH", Muted), 1, 0);
        _host.Margin = new Padding(0, 5, 14, 14);
        _user.Margin = new Padding(0, 5, 0, 14);
        fields.Controls.Add(_host, 0, 1);
        fields.Controls.Add(_user, 1, 1);
        root.Controls.Add(fields, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        actions.Controls.AddRange([_connect, _disconnect, _inspect, _save, _openMoonlight]);
        root.Controls.Add(actions, 0, 3);

        var pairing = MakeLabel("Primeiro acesso: clique em Parear e confirme o PIN na interface do Sunshine no Linux.", Muted);
        pairing.Dock = DockStyle.Fill;
        pairing.Margin = new Padding(0, 0, 0, 16);
        root.Controls.Add(pairing, 0, 4);

        root.Controls.Add(_shareClipboard, 0, 5);

        var logHeading = MakeLabel("ATIVIDADE", Muted);
        logHeading.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        logHeading.Margin = new Padding(0, 0, 0, 7);
        root.Controls.Add(logHeading, 0, 6);
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Surface;
        _log.ForeColor = Muted;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9);
        _log.Margin = Padding.Empty;
        _log.MinimumSize = new Size(0, 100);
        _log.AccessibleName = "Registro de atividade";
        root.Controls.Add(_log, 0, 7);
        Controls.Add(root);

        _tips.SetToolTip(_host, "Endereço IPv4 privado do computador Linux na mesma rede.");
        _tips.SetToolTip(_user, "Usuário já configurado para acesso SSH neste Windows.");
        _tips.SetToolTip(_openMoonlight, "Inicia o host e abre o pareamento do Moonlight. Confirme o PIN no Sunshine.");
        _tips.SetToolTip(_disconnect, "Encerra a sessão e libera a GPU usada pelo host.");
    }

    private void BindActions()
    {
        _shareClipboard.CheckedChanged += async (_, _) =>
        {
            if (_controller.IsSessionOpen)
                await RunOperationAsync(() => _controller.SetClipboardSharingAsync(_shareClipboard.Checked));
        };
        _save.Click += (_, _) => SaveProfile();
        _inspect.Click += async (_, _) =>
        {
            if (ReadProfile(requireMoonlight: false) is { } profile)
            {
                await RunOperationAsync(async () => ApplyStatus(await _controller.InspectAsync(profile)));
            }
        };
        _connect.Click += async (_, _) =>
        {
            if (ReadProfile() is { } profile)
            {
                await RunOperationAsync(async () =>
                {
                    ProfileStore.Save(profile);
                    await _controller.ConnectAsync(profile);
                    if (_controller.IsSessionOpen)
                    {
                        _tray.Visible = true;
                        Hide();
                    }
                });
            }
        };
        _disconnect.Click += async (_, _) =>
        {
            if (_busy) _controller.CancelPendingConnection();
            else await RunOperationAsync(_controller.DisconnectAsync);
        };
        _openMoonlight.Click += async (_, _) =>
        {
            if (ReadProfile() is not { } profile)
            {
                return;
            }
            await RunOperationAsync(async () =>
            {
                ProfileStore.Save(profile);
                await _controller.PairAsync(profile);
            });
        };
        FormClosing += OnFormClosing;
    }

    private void LoadProfile()
    {
        try
        {
            var profile = ProfileStore.Load();
            _host.Text = profile.Host;
            _user.Text = profile.User;
            _viewerPath = profile.MoonlightPath;
            _shareClipboard.Checked = profile.ShareClipboard;
            AppendLog(string.IsNullOrWhiteSpace(profile.Host) && string.IsNullOrWhiteSpace(profile.User)
                ? "Informe o Linux e o usuário SSH para começar."
                : "Perfil local carregado.");
        }
        catch (Exception ex)
        {
            AppendLog($"Não foi possível carregar o perfil: {ex.Message}");
        }
    }

    private Profile? ReadProfile(bool requireMoonlight = true)
    {
        var profile = new Profile(_host.Text, _user.Text,
            ProfileStore.ResolveMoonlightPath(_viewerPath), _shareClipboard.Checked).Normalize();
        if (profile.Validate(requireMoonlight) is { } error)
        {
            ShowError(error);
            return null;
        }
        return profile;
    }

    private void SaveProfile()
    {
        if (ReadProfile(requireMoonlight: false) is not { } profile)
        {
            return;
        }
        try
        {
            ProfileStore.Save(profile);
            AppendLog("Perfil salvo neste Windows.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_busy || _closing)
        {
            return;
        }
        _busy = true;
        SetControlsEnabled(false);
        UseWaitCursor = true;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            SetControlsEnabled(true);
            if (_closeRequested)
            {
                Close();
            }
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _host, _user, _save, _inspect,
            _connect, _disconnect, _openMoonlight, _shareClipboard
        })
        {
            control.Enabled = enabled;
        }
        _disconnect.Enabled = !_closing;
    }

    private void OnControllerLog(string message) => OnUiThread(() => AppendLog(message));

    private void OnControllerStatus(SessionStatus status) => OnUiThread(() => ApplyStatus(status));

    private void OnSessionEnded() => OnUiThread(() =>
    {
        _tray.Visible = false;
        if (!_closing && !_closeRequested) RestoreLauncher();
    });

    private void RestoreLauncher()
    {
        Show();
        WindowState = FormWindowState.Normal;
    }

    private void OnUiThread(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && !Disposing)
                    {
                        action();
                    }
                });
            }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
            {
                // The window may have closed while an operation finished.
            }
        }
        else
        {
            action();
        }
    }

    private void ApplyStatus(SessionStatus status)
    {
        _sessionState.Text = status.State;
        _gpuState.Text = status.GpuState;
        _detail.Text = status.Detail;
    }

    private void AppendLog(string message)
    {
        foreach (var line in message.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {line[..Math.Min(line.Length, 1500)]}");
        }
        while (_logLines.Count > 120)
        {
            _logLines.Dequeue();
        }
        _log.Text = string.Join(Environment.NewLine, _logLines);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ShowError(string message)
    {
        AppendLog(message);
        _detail.Text = message;
        MessageBox.Show(this, message, "LanView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }
        e.Cancel = true;
        _closeRequested = true;
        if (_closing)
        {
            return;
        }
        if (_busy)
        {
            _controller.CancelPendingConnection();
            AppendLog("Cancelando a abertura e liberando a sessão.");
            return;
        }

        _closing = true;
        SetControlsEnabled(false);
        _detail.Text = "Encerrando a sessão…";
        try
        {
            await _controller.DisconnectAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _closeRequested = false;
            _closing = false;
            SetControlsEnabled(true);
            ShowError($"Não foi possível encerrar a sessão: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _controller.Log -= OnControllerLog;
            _controller.StatusChanged -= OnControllerStatus;
            _controller.SessionEnded -= OnSessionEnded;
            _controller.Dispose();
            _tray.Visible = false;
            _tray.Icon = null;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            Icon = null;
            _applicationIcon.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private static TextBox Input(string accessibleName) => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Surface,
        ForeColor = Foreground,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 11),
        AccessibleName = accessibleName
    };

    private static Label MakeLabel(string value, Color color) => new()
    {
        Text = value,
        AutoSize = true,
        ForeColor = color,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private static Label StatusValue(string value)
    {
        var label = MakeLabel(value, Foreground);
        label.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        label.Dock = DockStyle.Fill;
        label.Margin = new Padding(0, 5, 0, 0);
        return label;
    }

    private static Button ActionButton(string caption, bool primary = false)
    {
        var button = new Button
        {
            Text = caption,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Background : Foreground,
            Padding = new Padding(12, 7, 12, 7),
            Margin = new Padding(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(137, 189, 255)
            : Color.FromArgb(42, 49, 60);
        button.FlatAppearance.MouseDownBackColor = primary
            ? Color.FromArgb(80, 143, 226)
            : Color.FromArgb(50, 59, 72);
        return button;
    }
}
