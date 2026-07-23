using System.Drawing.Drawing2D;
using System.Net;

namespace StreamDeckRelay;

/// <summary>
/// System-tray UI: shows relay status, lets the user override the auto-detected
/// mode, open the settings window, and toggle start-with-Windows. On first run
/// the setup window is shown before the relay starts.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _modeAuto;
    private readonly ToolStripMenuItem _modeHost;
    private readonly ToolStripMenuItem _modeClient;
    private readonly ToolStripMenuItem _startupItem;

    private readonly Settings _settings = Settings.Load();
    private readonly List<string> _recentLog = [];

    private CancellationTokenSource _relayCts = new();
    private RelayMode _activeMode = RelayMode.Auto; // resolved mode actually running
    private volatile IPEndPoint? _discoveredHost;
    private SetupWindow? _openSettingsWindow;
    private readonly ContextMenuStrip _menu;

    public TrayAppContext()
    {
        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _modeAuto = new ToolStripMenuItem("Automatic", null, (_, _) => SetMode(RelayMode.Auto));
        _modeHost = new ToolStripMenuItem("Host (Stream Deck is on this PC)", null, (_, _) => SetMode(RelayMode.Host));
        _modeClient = new ToolStripMenuItem("Client (Stream Deck is on another PC)", null, (_, _) => SetMode(RelayMode.Client));
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = SetupWindow.IsStartupEnabled(),
        };

        var menu = _menu = new ContextMenuStrip();
        _ = _menu.Handle; // force handle creation on the UI thread so RequestShowSettings can marshal onto it
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => _ = ShowSettingsAsync()));
        var modeMenu = new ToolStripMenuItem("Mode");
        modeMenu.DropDownItems.AddRange([_modeAuto, _modeHost, _modeClient]);
        menu.Items.Add(modeMenu);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Show log...", null, (_, _) => ShowLog()));
        menu.Items.Add(new ToolStripMenuItem("About...", null, (_, _) => AboutDialog.Show()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Icon = MakeIcon(Color.Gray),
            Text = "Stream Deck Relay",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => _ = ShowSettingsAsync();

        RelayCore.Log += AddLog;
        RelayCore.ActiveClientsChanged += OnActiveClientsChanged;
        RelayCore.ClientSeen += OnClientPcSeen;
        Discovery.ProbeAnswered += OnClientPcSeen;

        // drop client PCs from the count once they stop probing
        var presenceTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        presenceTimer.Tick += (_, _) => UpdateHostStatus();
        presenceTimer.Start();

        _ = InitializeAsync();
    }

    // ------------------------------------------------- host-side indicators

    // client PCs are considered present while they keep probing (every ~30s)
    // or actively connect; entries expire after this window
    private static readonly TimeSpan ClientPresenceWindow = TimeSpan.FromSeconds(90);

    private readonly Dictionary<string, DateTime> _clientPcsLastSeen = [];
    private readonly HashSet<string> _announcedClientAddresses = [];
    private int _connectedApps;
    private int _lastClientCount;

    private void OnClientPcSeen(IPAddress address) => OnClientPcSeen(address.ToString());

    private void OnClientPcSeen(string address)
    {
        if (_activeMode != RelayMode.Host) return;
        bool isNew;
        lock (_clientPcsLastSeen)
        {
            _clientPcsLastSeen[address] = DateTime.UtcNow;
            isNew = _announcedClientAddresses.Add(address);
        }
        if (isNew)
        {
            AddLog($"Client PC at {address} discovered this host.");
            Notify($"A client PC at {address} found this Stream Deck host.");
        }
        UpdateHostStatus();
    }

    private void OnActiveClientsChanged(int count)
    {
        if (_activeMode != RelayMode.Host) return;
        if (count > _lastClientCount && count == 1)
            Notify("A remote app connected to this Stream Deck.");
        _lastClientCount = _connectedApps = count;
        UpdateHostStatus();
    }

    private void UpdateHostStatus()
    {
        if (_activeMode != RelayMode.Host) return;

        int pcs;
        lock (_clientPcsLastSeen)
        {
            var cutoff = DateTime.UtcNow - ClientPresenceWindow;
            foreach (var stale in _clientPcsLastSeen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _clientPcsLastSeen.Remove(stale);
            pcs = _clientPcsLastSeen.Count;
        }

        var apps = _connectedApps;
        var status = pcs == 0 && apps == 0
            ? $"Hosting on port {_settings.Port} - waiting for clients"
            : $"Hosting on port {_settings.Port} - {pcs} client PC{(pcs == 1 ? "" : "s")}, {apps} app{(apps == 1 ? "" : "s")} connected";
        SetStatus(status, apps > 0 ? Color.MediumSeaGreen : pcs > 0 ? Color.YellowGreen : Color.CadetBlue);
    }

    /// <summary>Called (from any thread) when a second exe launch asks to show the UI.</summary>
    public void RequestShowSettings() =>
        _menu.BeginInvoke(() => _ = ShowSettingsAsync());

    // ------------------------------------------------------------ lifecycle

    private async Task InitializeAsync()
    {
        if (!_settings.SetupCompleted)
        {
            await ShowSettingsAsync(firstRun: true);
            if (!_settings.SetupCompleted)
            {
                // window closed without saving: keep sane defaults and continue
                _settings.SetupCompleted = true;
                _settings.Save();
            }
        }
        _ = StartRelayAsync();
    }

    private async Task ShowSettingsAsync(bool firstRun = false)
    {
        if (_openSettingsWindow is { } open)
        {
            open.Activate(); // already showing - just bring it to the front
            return;
        }
        try
        {
            SetStatus("Detecting mode...", Color.Gray);
            var detected = await RelayCore.LocalStreamDeckPipeExistsAsync() ? RelayMode.Host : RelayMode.Client;

            using var window = new SetupWindow(_settings, detected, firstRun);
            _openSettingsWindow = window;
            window.ShowDialog();
            _startupItem.Checked = SetupWindow.IsStartupEnabled();
            if (window.Saved && !firstRun) _ = StartRelayAsync();
        }
        finally
        {
            _openSettingsWindow = null;
        }
    }

    private async Task StartRelayAsync()
    {
        _relayCts.Cancel();
        _relayCts = new CancellationTokenSource();
        var ct = _relayCts.Token;

        var mode = _settings.Mode;
        if (mode == RelayMode.Auto)
        {
            SetStatus("Detecting mode...", Color.Gray);
            mode = await RelayCore.LocalStreamDeckPipeExistsAsync() ? RelayMode.Host : RelayMode.Client;
            AddLog($"Auto-detected mode: {mode} ({(mode == RelayMode.Host ? "found" : "no")} local Stream Deck pipe)");
        }

        _activeMode = mode;
        UpdateMenuState();

        try
        {
            if (mode == RelayMode.Host)
            {
                SetStatus($"Hosting Stream Deck on port {_settings.Port}", Color.MediumSeaGreen);
                Notify("Acting as host - sharing this PC's Stream Deck on the network.");
                await Task.WhenAll(
                    RelayCore.RunHostAsync(_settings.Port, ct),
                    Discovery.RunResponderAsync(_settings.Port, ct));
            }
            else
            {
                Notify("Acting as client - looking for the Stream Deck PC on your network.");
                await Task.WhenAll(
                    RelayCore.RunClientAsync(ResolveHostAsync, ct),
                    RunDiscoveryLoopAsync(ct));
            }
        }
        catch (OperationCanceledException)
        {
            // mode change or exit
        }
        catch (Exception ex)
        {
            AddLog($"Relay stopped: {ex.Message}");
            SetStatus($"Error: {ex.Message}", Color.IndianRed);
        }
    }

    /// <summary>Client mode: keep looking for the host PC and reflect it in the status.</summary>
    private async Task RunDiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (ManualHost() is { } manual)
            {
                _discoveredHost = manual;
                SetStatus($"Using host {manual} (manual)", Color.MediumSeaGreen);
                return; // manual address set - nothing to discover
            }

            SetStatus("Searching for Stream Deck PC...", Color.Goldenrod);
            var host = await Discovery.FindHostAsync(TimeSpan.FromSeconds(2), ct);
            if (host is not null)
            {
                if (!host.Equals(_discoveredHost))
                {
                    _discoveredHost = host;
                    AddLog($"Found host PC at {host}");
                    Notify($"Found the Stream Deck PC at {host.Address}.");
                }
                SetStatus($"Ready - Stream Deck host at {host}", Color.MediumSeaGreen);
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // periodic re-check
            }
            else
            {
                _discoveredHost = null;
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private IPEndPoint? ManualHost()
    {
        if (string.IsNullOrWhiteSpace(_settings.ManualHostAddress)) return null;
        return IPEndPoint.TryParse(_settings.ManualHostAddress, out var withPort) && withPort.Port != 0
            ? withPort
            : IPAddress.TryParse(_settings.ManualHostAddress, out var ip)
                ? new IPEndPoint(ip, _settings.Port)
                : null;
    }

    private async Task<IPEndPoint?> ResolveHostAsync(CancellationToken ct)
    {
        if (ManualHost() is { } manual) return manual;
        return _discoveredHost ?? await Discovery.FindHostAsync(TimeSpan.FromSeconds(2), ct);
    }

    // ------------------------------------------------------------------ ui

    private void SetMode(RelayMode mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        _ = StartRelayAsync();
    }

    private void UpdateMenuState()
    {
        _modeAuto.Checked = _settings.Mode == RelayMode.Auto;
        _modeHost.Checked = _settings.Mode == RelayMode.Host;
        _modeClient.Checked = _settings.Mode == RelayMode.Client;
        if (_settings.Mode == RelayMode.Auto)
            _modeAuto.Text = $"Automatic (currently: {_activeMode})";
    }

    private void ToggleStartup()
    {
        var enable = !_startupItem.Checked;
        SetupWindow.SetStartupEnabled(enable);
        _startupItem.Checked = enable;
    }

    private void SetStatus(string text, Color color)
    {
        void Apply()
        {
            _statusItem.Text = text;
            var tooltip = $"Stream Deck Relay - {text}";
            _trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
            var old = _trayIcon.Icon;
            _trayIcon.Icon = MakeIcon(color);
            old?.Dispose();
            UpdateMenuState();
        }

        if (_statusItem.Owner is { InvokeRequired: true } owner) owner.Invoke(Apply);
        else Apply();
    }

    private void Notify(string message) =>
        _trayIcon.ShowBalloonTip(4000, "Stream Deck Relay", message, ToolTipIcon.Info);

    private void AddLog(string message)
    {
        lock (_recentLog)
        {
            _recentLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_recentLog.Count > 500) _recentLog.RemoveAt(0);
        }
    }

    private void ShowLog()
    {
        string text;
        lock (_recentLog) text = string.Join(Environment.NewLine, _recentLog);
        LogWindow.Show("Stream Deck Relay - Log", text.Length > 0 ? text : "(nothing yet)");
    }

    private void ExitApplication()
    {
        _relayCts.Cancel();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    /// <summary>Simple generated tray icon: rounded square with "SDR", tinted by state.</summary>
    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var path = RoundedRect(new Rectangle(2, 2, 28, 28), 8);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("SDR", font);
            g.DrawString("SDR", font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2 + 1);
        }
        var handle = bmp.GetHicon();
        try
        {
            // clone so we can release the GDI handle deterministically
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
