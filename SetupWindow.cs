using Microsoft.Win32;

namespace StreamDeckRelay;

/// <summary>
/// Setup/settings window. Shown automatically on first run (pre-selecting the
/// auto-detected mode) and available afterwards via the tray "Settings..."
/// item. On save the app (re)starts the relay and lives in the tray.
/// </summary>
internal sealed class SetupWindow : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "StreamDeckRelay";

    private readonly Settings _settings;
    private readonly RadioButton _auto;
    private readonly RadioButton _host;
    private readonly RadioButton _client;
    private readonly TextBox _hostAddress;
    private readonly Label _hostAddressLabel;
    private readonly CheckBox _startup;

    /// <summary>True when the user pressed Save (settings were applied).</summary>
    public bool Saved { get; private set; }

    public SetupWindow(Settings settings, RelayMode detectedMode, bool firstRun)
    {
        _settings = settings;

        Text = firstRun ? "Stream Deck Relay - Setup" : "Stream Deck Relay - Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(420, 356);
        Font = new Font("Segoe UI", 9);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep default */ }

        var title = new Label
        {
            Text = "Stream Deck Relay",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            AutoSize = false,
            Bounds = new Rectangle(16, 12, 388, 30),
        };
        var subtitle = new Label
        {
            Text = "Created by Crowd Control - use your Stream Deck from a second PC.",
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
            Bounds = new Rectangle(16, 42, 388, 18),
        };

        var detection = new Label
        {
            Text = detectedMode == RelayMode.Host
                ? "A Stream Deck was found on this PC - it will act as the host and\nshare the Stream Deck on your network."
                : "No Stream Deck was found on this PC - it will act as the client and\nconnect to the PC that has the Stream Deck.",
            AutoSize = false,
            Bounds = new Rectangle(16, 66, 388, 34),
        };

        var modeGroup = new GroupBox { Text = "Mode", Bounds = new Rectangle(16, 106, 388, 118) };
        _auto = new RadioButton
        {
            Text = $"Automatic (recommended) - detected: {detectedMode}",
            Bounds = new Rectangle(12, 22, 364, 22),
        };
        _host = new RadioButton
        {
            Text = "Host - the Stream Deck is plugged into this PC",
            Bounds = new Rectangle(12, 50, 364, 22),
        };
        _client = new RadioButton
        {
            Text = "Client - the Stream Deck is on another PC",
            Bounds = new Rectangle(12, 78, 364, 22),
        };
        modeGroup.Controls.AddRange([_auto, _host, _client]);

        _hostAddressLabel = new Label
        {
            Text = "Host PC address (leave empty to find it automatically):",
            AutoSize = false,
            Bounds = new Rectangle(16, 236, 388, 18),
        };
        _hostAddress = new TextBox
        {
            Bounds = new Rectangle(16, 256, 388, 24),
            PlaceholderText = "auto-discover on your network",
            Text = settings.ManualHostAddress ?? "",
        };

        _startup = new CheckBox
        {
            Text = "Start with Windows",
            Bounds = new Rectangle(16, 288, 200, 22),
            Checked = firstRun || IsStartupEnabled(),
        };

        var save = new Button
        {
            Text = firstRun ? "Save && Start" : "Save",
            Bounds = new Rectangle(224, 318, 100, 28),
        };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(330, 318, 74, 28),
            Visible = !firstRun,
        };

        Controls.AddRange([title, subtitle, detection, modeGroup, _hostAddressLabel, _hostAddress, _startup, save, cancel]);
        AcceptButton = save;
        if (!firstRun) CancelButton = cancel;

        switch (settings.Mode)
        {
            case RelayMode.Host: _host.Checked = true; break;
            case RelayMode.Client: _client.Checked = true; break;
            default: _auto.Checked = true; break;
        }

        // host address only matters when acting as client
        void UpdateHostAddressVisibility()
        {
            var clientish = _client.Checked || (_auto.Checked && detectedMode == RelayMode.Client);
            _hostAddress.Enabled = clientish;
            _hostAddressLabel.Enabled = clientish;
        }
        _auto.CheckedChanged += (_, _) => UpdateHostAddressVisibility();
        _host.CheckedChanged += (_, _) => UpdateHostAddressVisibility();
        _client.CheckedChanged += (_, _) => UpdateHostAddressVisibility();
        UpdateHostAddressVisibility();
    }

    private void SaveAndClose()
    {
        _settings.Mode = _host.Checked ? RelayMode.Host : _client.Checked ? RelayMode.Client : RelayMode.Auto;
        _settings.ManualHostAddress = string.IsNullOrWhiteSpace(_hostAddress.Text) ? null : _hostAddress.Text.Trim();
        _settings.SetupCompleted = true;
        _settings.Save();
        SetStartupEnabled(_startup.Checked);
        Saved = true;
        Close();
    }

    // ------------------------------------------------- startup registration

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunKeyName) is string;
    }

    public static void SetStartupEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
            key.SetValue(RunKeyName, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(RunKeyName, throwOnMissingValue: false);
    }
}
