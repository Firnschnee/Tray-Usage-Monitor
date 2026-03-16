# Security & QA Audit — ClaudeUsageMonitor v0.4.8

Erstellt: 2026-03-16

---

## SECURITY

### S1 — PATH-Hijacking-Risiko (LOW)
**`ClaudeCodeInfo.cs:22`**
```csharp
var psi = new ProcessStartInfo("claude", "--version")
```
`"claude"` wird über PATH aufgelöst. Ein lokaler Angreifer mit Schreibrecht in einem PATH-Verzeichnis (z.B. `%APPDATA%\Local\Microsoft\WindowsApps`) könnte eine bösartige `claude.exe` platzieren. Da das Kommando fest kodiert ist (keine User-Eingabe), gibt es kein direktes Injection-Risiko — aber die PATH-Auflösung ist eine Angriffsfläche. **Priorität: Low** (erfordert lokalen Zugriff).

### S2 — Crash-Log in world-readable %TEMP% (LOW)
**`Program.cs:21-24`**
```csharp
var logPath = Path.Combine(Path.GetTempPath(), "claude-usage-monitor-crash.log");
File.WriteAllText(logPath, $"{DateTime.Now:u}\n{e.ExceptionObject}");
```
Stack-Traces können Dateipfade, interne Zustände oder (im Fehlerfall) Token-bezogene Fehlermeldungen enthalten. `%TEMP%` ist auf shared Systemen für alle User lesbar. **Priorität: Low** (Einzelplatz-Tool).

### S3 — OAuth-Token als Plain `string` im Heap (INFO)
**`CredentialReader.cs:47`, `UsageFetcher.cs:31`**
Der Token wird als `string` übergeben und lebt im managed Heap. `SecureString` würde die Verweildauer im Speicher reduzieren, ist aber für Desktop-Tools ohne erhöhte Rechte-Anforderungen akzeptabel. **Priorität: Info**.

### S4 — Vollständige API-Antwort in Debug-Log (INFO)
**`UsageFetcher.cs:47`**
```csharp
#if DEBUG
System.Diagnostics.Debug.WriteLine($"[Usage] Response: {json}");
#endif
```
`json` enthält Usage-Daten incl. `resets_at`-Timestamps. Durch `#if DEBUG` nur in Debug-Builds aktiv — korrekt abgesichert. **Priorität: Info (kein Handlungsbedarf)**.

---

## QA — BUGS

### Q1 — GDI/Font-Leak in `RefreshWidget()` (MITTEL)
**`MainForm.cs:301`**
```csharp
_widget.Controls.Clear(); // ← disposed NICHT die Controls
```
`Controls.Clear()` entfernt Controls aus der Collection, **ohne** sie zu `Dispose()`. Jede Aktualisierung (alle 2 Minuten) erstellt neue `Label`-, `Panel`- und `Font`-Objekte, die danach nie freigegeben werden. Zusätzlich halten die `bar.Paint`-Lambda-Closures Referenzen auf `color` und `markers`. Über Stunden akkumuliert das GDI-Handles und managed Finalizer-Pressure.

**Fix:** `Controls.Clear()` durch eine Dispose-Schleife ersetzen:
```csharp
while (_widget.Controls.Count > 0) { var c = _widget.Controls[0]; _widget.Controls.RemoveAt(0); c.Dispose(); }
```

### Q2 — `ReadToEnd()` vor `WaitForExit()` — Timeout wirkungslos (MITTEL)
**`ClaudeCodeInfo.cs:29-30`**
```csharp
var output = proc.StandardOutput.ReadToEnd().Trim(); // blockiert bis stdout geschlossen
proc.WaitForExit(3000);                              // zu spät — wird ggf. nie erreicht
```
`ReadToEnd()` blockiert bis der Prozess stdout schließt. `WaitForExit(3000)` danach ist effektiv ohne Funktion. Hängt ein manipuliertes `claude`-Binary oder schreibt es unbegrenzt auf stdout, friert die App ein.

**Fix:** Reihenfolge tauschen: erst `WaitForExit`, dann lesen — oder `ReadToEnd` durch `ReadLine`/async-Pattern ersetzen.

### Q3 — Doppelter Separator im Kontextmenü (NIEDRIG)
**`MainForm.cs:178-180`**
```csharp
m.Items.Add(new ToolStripSeparator());
m.Items.Add(new ToolStripSeparator()); // ← doppelt, kein Menüeintrag dazwischen
```
Erzeugt zwei direkt aufeinanderfolgende Trennlinien im Menü. Einer der beiden ist überflüssig.

### Q4 — About-Dialog nicht disposed (NIEDRIG)
**`MainForm.cs:294`**
```csharp
dlg.ShowDialog(); // nach Rückkehr: dlg nicht disposed
```
`ShowDialog()` schließt den Dialog, aber der Finalizer läuft erst beim GC. Korrekt wäre `using var dlg = new Form { ... };` oder explizites `dlg.Dispose()` danach.

### Q5 — `_ = PollAsync()` ohne `FireAndForget` (NIEDRIG)
**`MainForm.cs:204, 239`**
```csharp
_ = PollAsync(); // unbeobachteter Task
```
An zwei Stellen wird `PollAsync()` direkt aufgerufen statt über `FireAndForget(PollAsync)`. `PollAsync` hat zwar eigene try/catch-Blöcke, aber unbeobachtete Tasks können in bestimmten Konfigurationen (`TaskScheduler.UnobservedTaskException`) zu Absturz führen. Inkonsistent mit dem restlichen Code.

---

## QA — WARNUNGEN (kein Bug, aber Auffälligkeit)

### W1 — Redundante Doppel-Truncation des Tooltips
**`UsageData.cs:62`** und **`MainForm.cs:383`**
`TooltipText` kürzt bereits auf 127 Zeichen; `TruncateTooltip()` in `MainForm` kürzt nochmals. Der zweite Aufruf ist immer ein No-op. Der `TruncateTooltip`-Code in `MainForm` ist toter Code.

### W2 — `_widget` wird nach Schließen nicht disposed
**`MainForm.cs:224`**
```csharp
_widget.FormClosed += (_, _) => _widget = null;
```
`Form`s, die mit `Show()` (nicht `ShowDialog()`) geöffnet werden, werden in WinForms **nicht** automatisch disposed. Der GC collect das Objekt letztendlich, aber der Finalizer-Pfad ist nicht deterministisch. Besser: `_widget.FormClosed += (_, _) => { _widget?.Dispose(); _widget = null; };`

---

## Zusammenfassung

| ID | Schwere | Datei | Beschreibung |
|----|---------|-------|--------------|
| Q1 | **Mittel** | `MainForm.cs:301` | GDI/Font-Leak bei jedem Refresh via `Controls.Clear()` |
| Q2 | **Mittel** | `ClaudeCodeInfo.cs:29` | `ReadToEnd()` vor `WaitForExit` — Timeout wirkungslos |
| S1 | Low | `ClaudeCodeInfo.cs:22` | PATH-Hijacking über `claude`-Binary |
| Q3 | Low | `MainForm.cs:178` | Doppelter Trennstrich im Menü |
| Q4 | Low | `MainForm.cs:294` | About-Dialog nicht disposed |
| Q5 | Low | `MainForm.cs:204,239` | `_ = PollAsync()` statt `FireAndForget` |
| S2 | Low | `Program.cs:21` | Crash-Log in %TEMP% (world-readable) |
| W1 | Info | `MainForm.cs:383` | Redundante Tooltip-Truncation (toter Code) |
| W2 | Info | `MainForm.cs:224` | `_widget` nach Schließen nicht disposed |
| S3 | Info | `CredentialReader.cs` | Token als Plain `string` (akzeptabel) |

**Kritische Sicherheitslücken: keine.** Der Code ist für ein lokales Desktop-Monitoring-Tool solide.
Die zwei mittelschweren QA-Findings (Q1, Q2) sollten behoben werden, da Q1 zu einem langlebigen
GDI-Objekt-Leak führt.
