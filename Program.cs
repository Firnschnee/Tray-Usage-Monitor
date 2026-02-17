namespace ClaudeUsageMonitor;

internal static class Program
{
    private const string MutexName = "ClaudeUsageMonitor_v2";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Claude Usage Monitor läuft bereits.",
                "Bereits aktiv", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.ThreadException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"UI Exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"Unhandled: {e.ExceptionObject}");

        Application.Run(new MainForm());
    }
}
