using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeUsageMonitor;

/// <summary>
/// Login-Fenster mit eingebettetem WebView2-Browser.
/// 
/// Funktionsweise (identisch zum Electron-Widget):
/// 1. Öffnet https://claude.ai in einem echten Chromium-Browser
/// 2. User loggt sich normal ein (Google OAuth, E-Mail, etc.)
/// 3. WebView2 hat Zugriff auf alle Cookies inkl. sessionKey
/// 4. Sobald sessionKey erkannt wird → automatisch extrahieren
/// 5. Org-ID via API abrufen → fertig, Fenster schließen
/// 
/// Vorteile gegenüber manuellem Cookie-Kopieren:
/// - Keine DevTools nötig
/// - Funktioniert mit allen Auth-Methoden (Google, SSO, etc.)
/// - Keine Non-ASCII-Probleme
/// - Silent Re-Login möglich (existierende OAuth-Session)
/// </summary>
public sealed class LoginForm : Form
{
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _cookieCheckTimer;
    private readonly ClaudeApiClient _apiClient;
    private bool _loginComplete;

    /// <summary>Wird gesetzt wenn Login erfolgreich war.</summary>
    public string? SessionKey { get; private set; }
    public string? OrganizationId { get; private set; }

    /// <summary>
    /// Erstellt ein Login-Fenster.
    /// silent=true → verstecktes Fenster für automatischen Re-Login.
    /// </summary>
    public LoginForm(bool silent = false)
    {
        Text = "Claude.ai Login";
        Size = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Shield;
        MinimumSize = new Size(600, 400);

        if (silent)
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
        }

        _apiClient = new ClaudeApiClient();

        // WebView2 Control
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
        };
        _webView.CoreWebView2InitializationCompleted += OnWebViewReady;
        Controls.Add(_webView);

        // Timer: Alle 2s Cookies prüfen
        _cookieCheckTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _cookieCheckTimer.Tick += async (_, _) => await CheckForSessionKeyAsync();

        // Timeout für Silent Login
        if (silent)
        {
            var timeout = new System.Windows.Forms.Timer { Interval = 15000 };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                if (!_loginComplete)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            };
            timeout.Start();
        }

        Load += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // WebView2 User Data in AppData (persistente Cookies!)
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeUsageMonitor", "WebView2Data");

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _webView.EnsureCoreWebView2Async(env);

            // Navigation starten
            _webView.CoreWebView2.Navigate("https://claude.ai");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 konnte nicht initialisiert werden.\n\n" +
                $"Bitte stelle sicher, dass die WebView2 Runtime installiert ist:\n" +
                $"https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                $"Fehler: {ex.Message}",
                "WebView2 Fehler",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Abort;
            Close();
        }
    }

    private void OnWebViewReady(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            MessageBox.Show("WebView2 Initialisierung fehlgeschlagen.", "Fehler",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Abort;
            Close();
            return;
        }

        // Cookie-Check starten sobald WebView bereit ist
        _cookieCheckTimer.Start();

        // Auch bei jeder Navigation prüfen
        _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            await CheckForSessionKeyAsync();
        };
    }

    /// <summary>
    /// Prüft ob ein sessionKey-Cookie vorhanden ist.
    /// Wenn ja: Org-ID abrufen und Login abschließen.
    /// Exakt gleiche Logik wie im Electron-Widget (main.js Zeile 86-141).
    /// </summary>
    private async Task CheckForSessionKeyAsync()
    {
        if (_loginComplete || _webView.CoreWebView2 == null) return;

        try
        {
            // Alle Cookies für claude.ai abrufen
            var cookies = await _webView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://claude.ai");

            // sessionKey suchen
            var sessionCookie = cookies.FirstOrDefault(c =>
                c.Name.Equals("sessionKey", StringComparison.OrdinalIgnoreCase));

            if (sessionCookie == null || string.IsNullOrWhiteSpace(sessionCookie.Value))
                return;

            var sessionKey = sessionCookie.Value;
            System.Diagnostics.Debug.WriteLine($"[Login] sessionKey gefunden: {sessionKey[..Math.Min(20, sessionKey.Length)]}...");

            // Org-ID via API abrufen
            _apiClient.SetSessionKey(sessionKey);
            string orgId;
            try
            {
                orgId = await _apiClient.GetOrganizationIdAsync();
            }
            catch
            {
                // API noch nicht bereit (z.B. Login noch nicht abgeschlossen)
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Login] Org-ID: {orgId}");

            // Erfolg!
            _loginComplete = true;
            _cookieCheckTimer.Stop();

            SessionKey = sessionKey;
            OrganizationId = orgId;

            // SessionKey sicher speichern
            AppSettings.SaveSessionKey(sessionKey);

            DialogResult = DialogResult.OK;
            BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Login] Check-Fehler: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cookieCheckTimer?.Dispose();
            _webView?.Dispose();
            _apiClient?.Dispose();
        }
        base.Dispose(disposing);
    }
}
