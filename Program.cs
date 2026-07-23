namespace StreamDeckRelay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // single instance - a second copy (e.g. double-clicking the exe while it
        // already sits in the tray) asks the running instance to show its window
        using var mutex = new Mutex(initiallyOwned: true, "StreamDeckRelay-SingleInstance", out var isNew);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "StreamDeckRelay-ShowUI");
        if (!isNew)
        {
            showSignal.Set();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var context = new TrayAppContext();

        var signalWatcher = new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                context.RequestShowSettings();
            }
        })
        { IsBackground = true, Name = "ShowUI signal watcher" };
        signalWatcher.Start();

        Application.Run(context);
    }
}
