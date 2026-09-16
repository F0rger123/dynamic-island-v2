using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows.Forms;

internal sealed record GoogleDesktopCredentials(string ClientId, string ClientSecret);

internal static class SetupState
{
    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIslandBridge");
    static string ConfigPath => Path.Combine(Root, "setup.json");
    internal static bool IsComplete { get { try { return File.Exists(ConfigPath) && JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement.GetProperty("completed").GetBoolean(); } catch { return false; } } }
    internal static void Save(bool completed, bool autoStart) { Directory.CreateDirectory(Root); File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new { completed, autoStart, version = 2 })); }
    internal static void ShowIfFirstRun() { if (!File.Exists(ConfigPath)) ShowAgain(); }
    internal static void ShowAgain()
    {
        var t = new Thread(() => { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new SetupWizard()); });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
    }
}

internal static class SecretStore
{
    static string GeminiPath => Path.Combine(SetupState.Root, "gemini-api.bin");
    internal static void SaveGemini(string key) { Directory.CreateDirectory(SetupState.Root); File.WriteAllBytes(GeminiPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser)); }
    internal static string? LoadGemini() { try { return File.Exists(GeminiPath) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(GeminiPath), null, DataProtectionScope.CurrentUser)) : null; } catch { return null; } }
}

internal static class CredentialStore
{
    static string GooglePath => Path.Combine(SetupState.Root, "google-client.bin");
    internal static void SaveGoogle(GoogleDesktopCredentials value)
    {
        Directory.CreateDirectory(SetupState.Root);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GooglePath, bytes);
    }
    internal static GoogleDesktopCredentials? LoadGoogle()
    {
        try { if (!File.Exists(GooglePath)) return null; var bytes = ProtectedData.Unprotect(File.ReadAllBytes(GooglePath), null, DataProtectionScope.CurrentUser); return JsonSerializer.Deserialize<GoogleDesktopCredentials>(bytes); } catch { return null; }
    }
}

internal static class CliDiscovery
{
    internal static string? Find(string name)
    {
        var names = new[] { name + ".exe", name + ".cmd", name + ".bat", name };
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\.npm-global");
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\npm");
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs");
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase)) foreach (var n in names) { try { var p = Path.Combine(dir, n); if (File.Exists(p)) return p; } catch { } }
        return null;
    }
    internal static string? Version(string path)
    { try { var psi = new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }; using var p = Process.Start(psi); if (p is null) return null; var text = p.StandardOutput.ReadLine(); p.WaitForExit(2500); return text; } catch { return null; } }
}

internal sealed class SetupWizard : Form
{
    readonly TabControl pages = new() { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
    readonly Label status = new() { AutoSize = false, Height = 28, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
    readonly CheckBox startup = new() { Text = "Start Dynamic Island Bridge automatically with Windows", AutoSize = true, Checked = true };
    readonly Label[] checks = new Label[8];
    public SetupWizard()
    {
        Text = "Dynamic Island v2 Setup"; Width = 720; Height = 510; MinimumSize = new Size(620, 420); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        pages.TabPages.Add(WelcomePage()); pages.TabPages.Add(CheckPage()); pages.TabPages.Add(GooglePage()); pages.TabPages.Add(AgentsPage()); pages.TabPages.Add(FinishPage());
        Controls.Add(pages); Controls.Add(status); pages.SelectedIndexChanged += (_, _) => { if (pages.SelectedIndex == 1) RefreshChecks(); };
    }
    static Label Heading(string text) => new() { Text = text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(28, 24) };
    static Button Button(string text, EventHandler action) { var b = new Button { Text = text, AutoSize = true, Height = 32 }; b.Click += action; return b; }
    TabPage Page(string title) { var p = new TabPage(title) { Padding = new Padding(28) }; p.Controls.Add(Heading(title)); return p; }
    TabPage WelcomePage()
    {
        var p = Page("Welcome"); p.Controls.Add(new Label { Text = "Dynamic Island v2\n\nA simple setup for Google Calendar, Claude Code, Codex, Gemini, and startup behavior.", AutoSize = true, Location = new Point(32, 88), Font = new Font("Segoe UI", 11) });
        var list = new Label { Text = "✓ Google Calendar\n✓ Claude Code\n✓ OpenAI Codex\n✓ Google Gemini\n✓ Startup behavior", AutoSize = true, Location = new Point(36, 190), Font = new Font("Segoe UI", 10) }; p.Controls.Add(list);
        var start = Button("Start Setup", (_, _) => pages.SelectedIndex = 1); start.Location = new Point(36, 310); p.Controls.Add(start); var skip = Button("Skip For Now", (_, _) => { SetupState.Save(false, false); Close(); }); skip.Location = new Point(150, 310); p.Controls.Add(skip); return p;
    }
    TabPage CheckPage()
    {
        var p = Page("System check"); for (int i = 0; i < checks.Length; i++) { checks[i] = new Label { AutoSize = false, Width = 590, Height = 36, Location = new Point(36, 72 + i * 42), Font = new Font("Segoe UI", 10) }; p.Controls.Add(checks[i]); }
        var windhawk = Button("Open Windhawk", (_, _) => Open("https://windhawk.net/")); windhawk.Location = new Point(190, 410); p.Controls.Add(windhawk); var copySource = Button("Copy mod source", (_, _) => { try { var path = Path.Combine(AppContext.BaseDirectory, "sourcecode"); Clipboard.SetText(File.ReadAllText(path)); status.Text = "✓ Windhawk source copied."; } catch { status.Text = "Source file is not included in this package."; } }); copySource.Location = new Point(305, 410); p.Controls.Add(copySource); var test = Button("Test All", (_, _) => { RefreshChecks(); status.Text = "✓ Local dependency checks refreshed. Provider tests run after setup."; }); test.Location = new Point(425, 410); p.Controls.Add(test); var next = Button("Next", (_, _) => pages.SelectedIndex = 2); next.Location = new Point(36, 410); p.Controls.Add(next); var back = Button("Back", (_, _) => pages.SelectedIndex = 0); back.Location = new Point(110, 410); p.Controls.Add(back); return p;
    }
    void RefreshChecks()
    {
        var rows = new (string, string?)[] { ("Windows", Environment.OSVersion.VersionString), (".NET runtime", Environment.Version.ToString()), ("Windhawk", "Install/enable the source mod"), ("Claude Code", CliDiscovery.Find("claude")), ("Codex CLI", CliDiscovery.Find("codex")), ("Gemini CLI", CliDiscovery.Find("gemini")), ("Git", CliDiscovery.Find("git")), ("DynamicIslandBridge", Application.ExecutablePath) };
        for (int i = 0; i < rows.Length; i++) checks[i].Text = $"{rows[i].Item1,-22} {(rows[i].Item2 is null ? "Not installed" : "✓ Installed  " + rows[i].Item2)}";
    }
    TabPage GooglePage()
    {
        var p = Page("Google Calendar"); p.Controls.Add(new Label { Text = "1. Open Google setup.\n2. Enable Calendar API and create a Desktop OAuth client.\n3. Select the downloaded credentials JSON file.\n4. Connect when ready.", AutoSize = true, Location = new Point(36, 76), Font = new Font("Segoe UI", 10) });
        var open = Button("Open Google Cloud Console", (_, _) => Open("https://console.cloud.google.com/apis/credentials")); open.Location = new Point(36, 178); p.Controls.Add(open);
        var api = Button("Open Calendar API", (_, _) => Open("https://console.cloud.google.com/apis/library/calendar-json.googleapis.com")); api.Location = new Point(220, 178); p.Controls.Add(api);
        var select = Button("Select credentials JSON", SelectGoogle); select.Location = new Point(36, 225); p.Controls.Add(select);
        var connect = Button("Connect Google Calendar", (_, _) => {
            if (CredentialStore.LoadGoogle() is null) { status.Text = "Select credentials JSON first."; return; }
            connect.Enabled = false; status.Text = "Browser sign-in is waiting...";
            _ = Task.Run(async () => { try { await Program.ConnectGoogleCalendar(); BeginInvoke(() => { status.Text = "✓ Google Calendar connected."; connect.Enabled = true; }); } catch (Exception ex) { BeginInvoke(() => { status.Text = "Calendar connection failed: " + ex.Message; connect.Enabled = true; }); } });
        }); connect.Location = new Point(210, 225); p.Controls.Add(connect);
        var back = Button("Back", (_, _) => pages.SelectedIndex = 1); back.Location = new Point(36, 310); p.Controls.Add(back); var next = Button("Next", (_, _) => pages.SelectedIndex = 3); next.Location = new Point(110, 310); p.Controls.Add(next); return p;
    }
    void SelectGoogle(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Google OAuth JSON|*.json", Title = "Select downloaded Google Desktop OAuth credentials" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { using var doc = JsonDocument.Parse(File.ReadAllText(dialog.FileName)); var root = doc.RootElement.TryGetProperty("installed", out var installed) ? installed : doc.RootElement.GetProperty("web"); var id = root.GetProperty("client_id").GetString(); var secret = root.GetProperty("client_secret").GetString(); if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw new InvalidDataException(); CredentialStore.SaveGoogle(new GoogleDesktopCredentials(id, secret)); status.Text = "✓ OAuth client saved with Windows DPAPI."; }
        catch { status.Text = "That file is not a valid Google Desktop OAuth JSON file."; }
    }
    TabPage AgentsPage()
    {
        var p = Page("Local coding agents"); p.Controls.Add(new Label { Text = "The bridge reuses each CLI's existing login. Use the official installer if a tool is missing.", AutoSize = true, Location = new Point(36, 76), Font = new Font("Segoe UI", 10) });
        var commands = new[] { "npm install -g @anthropic-ai/claude-code", "npm install -g @openai/codex", "npm install -g @google/gemini-cli" }; var names = new[] { "Claude Code", "Codex", "Gemini CLI" };
        for (int i = 0; i < 3; i++) { int index = i; var label = new Label { Text = names[i] + "  " + (CliDiscovery.Find(new[] { "claude", "codex", "gemini" }[i]) is null ? "Not detected" : "✓ Detected"), AutoSize = true, Location = new Point(38, 125 + i * 54), Font = new Font("Segoe UI", 10) }; p.Controls.Add(label); var copy = Button("Copy", (_, _) => { Clipboard.SetText(commands[index]); status.Text = "✓ Copied install command."; }); copy.Location = new Point(360, 120 + i * 54); p.Controls.Add(copy); var open = Button("Official setup", (_, _) => Open(new[] { "https://docs.anthropic.com/en/docs/claude-code/overview", "https://github.com/openai/codex", "https://github.com/google-gemini/gemini-cli" }[index])); open.Location = new Point(430, 120 + i * 54); p.Controls.Add(open); }
        p.Controls.Add(new Label { Text = "Optional Gemini API mode (stored with Windows DPAPI):", AutoSize = true, Location = new Point(38, 285) });
        var apiKey = new TextBox { Width = 300, UseSystemPasswordChar = true, Location = new Point(38, 310) }; p.Controls.Add(apiKey);
        var saveKey = Button("Save Gemini key", (_, _) => { if (!string.IsNullOrWhiteSpace(apiKey.Text)) { SecretStore.SaveGemini(apiKey.Text); apiKey.Clear(); status.Text = "✓ Gemini API key encrypted for this Windows user."; } }); saveKey.Location = new Point(345, 308); p.Controls.Add(saveKey);
        var openAi = Button("Open Google AI Studio", (_, _) => Open("https://aistudio.google.com/apikey")); openAi.Location = new Point(480, 308); p.Controls.Add(openAi);
        p.Controls.Add(startup); startup.Location = new Point(38, 350); var back = Button("Back", (_, _) => pages.SelectedIndex = 2); back.Location = new Point(36, 390); p.Controls.Add(back); var next = Button("Finish", (_, _) => { SetupState.Save(true, startup.Checked); ConfigureStartup(startup.Checked); pages.SelectedIndex = 4; }); next.Location = new Point(110, 390); p.Controls.Add(next); return p;
    }
    TabPage FinishPage()
    {
        var p = Page("Dynamic Island is ready"); p.Controls.Add(new Label { Text = "The bridge can now be started automatically and configured again later from Setup & Integrations.", AutoSize = true, Location = new Point(36, 90), Font = new Font("Segoe UI", 11) }); var done = Button("Finish Anyway", (_, _) => Close()); done.Location = new Point(36, 300); p.Controls.Add(done); return p;
    }
    static void ConfigureStartup(bool enabled)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true); if (key is null) return; if (enabled) key.SetValue("DynamicIslandBridge", "\"" + Application.ExecutablePath + "\""); else key.DeleteValue("DynamicIslandBridge", false); } catch { }
    }
    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}
