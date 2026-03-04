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

        Application.ThreadException += (_, e) =>
            MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}",
                "Claude Usage Monitor \u2013 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var logPath = Path.Combine(Path.GetTempPath(), "claude-usage-monitor-crash.log");
            File.WriteAllText(logPath, $"{DateTime.Now:u}\n{e.ExceptionObject}");
            MessageBox.Show($"A fatal error occurred. Details saved to:\n{logPath}",
                "Claude Usage Monitor \u2013 Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.Run(new MainForm());
    }
}
