namespace StreamDeckRelay;

/// <summary>About dialog: app name, version, Crowd Control credit, repo link.</summary>
internal static class AboutDialog
{
    public const string RepoUrl = "https://github.com/WarpWorld/StreamDeckRelay";

    public static void Show()
    {
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        using var form = new Form
        {
            Text = "About Stream Deck Relay",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(340, 170),
            TopMost = true,
        };
        try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep default */ }

        var title = new Label
        {
            Text = "Stream Deck Relay",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(12, 14, 316, 28),
        };
        var info = new Label
        {
            Text = $"Version {version}\nCreated by Crowd Control",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(12, 46, 316, 40),
        };
        var link = new LinkLabel
        {
            Text = "github.com/WarpWorld/StreamDeckRelay",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(12, 90, 316, 20),
        };
        link.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(132, 126, 75, 26) };

        form.Controls.AddRange([title, info, link, ok]);
        form.AcceptButton = ok;
        form.CancelButton = ok;
        form.ShowDialog();
    }
}

/// <summary>Read-only log viewer window.</summary>
internal static class LogWindow
{
    public static void Show(string title, string text)
    {
        var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(680, 420),
            ShowInTaskbar = true,
        };
        try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep default */ }
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9),
            Text = text,
        };
        form.Controls.Add(box);
        form.Shown += (_, _) =>
        {
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        };
        form.Show();
    }
}
