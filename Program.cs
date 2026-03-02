namespace ClaudeUsageMonitor;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Global\\{a3f7c2d1-84e6-4b09-9f3a-2c1e7d508b4f}", out bool isNew);
        if (!isNew) { MessageBox.Show("Already running.", "Claude Usage Monitor"); return; }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.ThreadException += (_, e) => System.Diagnostics.Debug.WriteLine($"UI: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => System.Diagnostics.Debug.WriteLine($"Fatal: {e.ExceptionObject}");

        Application.Run(new MainForm());
    }
}
