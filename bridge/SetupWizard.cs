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

internal sealed class SetupWizard : Form
{
    static readonly string[] AgentNames = { "claude", "codex", "gemini" };
    static readonly string[] AgentTitles = { "Claude Code", "Codex CLI", "Gemini CLI" };

    readonly TabControl pages = new() { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
    readonly Label status = new() { AutoSize = false, Height = 28, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
    readonly CheckBox startup = new() { Text = "Start Dynamic Island Bridge automatically with Windows", AutoSize = true, Checked = true };
    readonly Label[] checks = new Label[8];
    readonly Label[] agentStatusLabels = new Label[3];
    TextBox geminiKeyBox = null!;

    public SetupWizard()
    {
        Text = "Dynamic Island v2 Setup"; Width = 780; Height = 530; MinimumSize = new Size(680, 450); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        pages.TabPages.Add(WelcomePage()); pages.TabPages.Add(CheckPage()); pages.TabPages.Add(GooglePage()); pages.TabPages.Add(AgentsPage()); pages.TabPages.Add(FinishPage());
        Controls.Add(pages); Controls.Add(status); pages.SelectedIndexChanged += (_, _) => { if (pages.SelectedIndex == 1) RefreshChecks(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        for (var i = 0; i < 3; i++) RefreshAgentRow(i, force: false);
    }

    static Label Heading(string text) => new() { Text = text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(28, 24) };
    static Button Button(string text, EventHandler action) { var b = new Button { Text = text, AutoSize = true, Height = 30 }; b.Click += action; return b; }
    TabPage Page(string title) { var p = new TabPage(title) { Padding = new Padding(28) }; p.Controls.Add(Heading(title)); return p; }
    TabPage WelcomePage()
    {
        var p = Page("Welcome"); p.Controls.Add(new Label { Text = "Dynamic Island v2\n\nA simple setup for Google Calendar, Claude Code, Codex, Gemini, and startup behavior.", AutoSize = true, Location = new Point(32, 88), Font = new Font("Segoe UI", 11) });
        var list = new Label { Text = "✓ Google Calendar\n✓ Claude Code\n✓ OpenAI Codex\n✓ Google Gemini\n✓ Startup behavior", AutoSize = true, Location = new Point(36, 190), Font = new Font("Segoe UI", 10) }; p.Controls.Add(list);
        var start = Button("Start Setup", (_, _) => pages.SelectedIndex = 1); start.Location = new Point(36, 310); p.Controls.Add(start); var skip = Button("Skip For Now", (_, _) => { SetupState.Save(false, false); Close(); }); skip.Location = new Point(150, 310); p.Controls.Add(skip); return p;
    }
    TabPage CheckPage()
    {
        var p = Page("System check"); for (int i = 0; i < checks.Length; i++) { checks[i] = new Label { AutoSize = false, Width = 700, Height = 36, Location = new Point(36, 66 + i * 36), Font = new Font("Segoe UI", 9) }; p.Controls.Add(checks[i]); }
        var windhawk = Button("Open Windhawk", (_, _) => Open("https://windhawk.net/")); windhawk.Location = new Point(190, 380); p.Controls.Add(windhawk); var copySource = Button("Copy mod source", (_, _) => { try { var path = Path.Combine(AppContext.BaseDirectory, "sourcecode"); Clipboard.SetText(File.ReadAllText(path)); status.Text = "✓ Windhawk source copied."; } catch { status.Text = "Source file is not included in this package."; } }); copySource.Location = new Point(305, 380); p.Controls.Add(copySource); var test = Button("Test All", (_, _) => { status.Text = "Running provider tests (this executes each CLI's --version)..."; RefreshChecks(); for (var i = 0; i < 3; i++) RefreshAgentRow(i, force: true); }); test.Location = new Point(430, 380); p.Controls.Add(test); var next = Button("Next", (_, _) => pages.SelectedIndex = 2); next.Location = new Point(36, 380); p.Controls.Add(next); var back = Button("Back", (_, _) => pages.SelectedIndex = 0); back.Location = new Point(110, 380); p.Controls.Add(back); return p;
    }
    void RefreshChecks()
    {
        for (var i = 0; i < checks.Length; i++) checks[i].Text = "Checking...";
        _ = Task.Run(() =>
        {
            var claude = AgentReadiness.Check("claude");
            var codex = AgentReadiness.Check("codex");
            var gemini = AgentReadiness.Check("gemini");
            var git = CliDiscovery.Find("git");
            BeginInvoke(() => ApplyChecks(claude, codex, gemini, git));
        });
    }
    void ApplyChecks(AgentReadiness.AgentResult claude, AgentReadiness.AgentResult codex, AgentReadiness.AgentResult gemini, string? git)
    {
        string Describe(AgentReadiness.AgentResult r, bool apiFallback)
        {
            if (r.Path is null) return apiFallback && r.State == AgentReadiness.Ready ? "Ready via Gemini API (no CLI needed)" : "Not installed";
            var mark = r.State == AgentReadiness.Ready ? "✓ " : "";
            return mark + AgentReadiness.FormatState(r) + "  " + r.Path;
        }
        var rows = new (string, string)[]
        {
            ("Windows", Environment.OSVersion.VersionString),
            (".NET runtime", Environment.Version.ToString()),
            ("Windhawk", "Install/enable the source mod"),
            ("Claude Code", Describe(claude, false)),
            ("Codex CLI", Describe(codex, false)),
            ("Gemini CLI", Describe(gemini, true)),
            ("Git", git is null ? "Not installed" : "✓ Installed  " + git),
            ("DynamicIslandBridge", Application.ExecutablePath),
        };
        for (var i = 0; i < rows.Length; i++) checks[i].Text = $"{rows[i].Item1,-22}  {rows[i].Item2}";
    }
    TabPage GooglePage()
    {
        var p = Page("Google Calendar"); p.Controls.Add(new Label { Text = "1. Open Google setup.\n2. Enable Calendar API and create a Desktop OAuth client.\n3. Select the downloaded credentials JSON file.\n4. Connect when ready.", AutoSize = true, Location = new Point(36, 76), Font = new Font("Segoe UI", 10) });
        var open = Button("Open Google Cloud Console", (_, _) => Open("https://console.cloud.google.com/apis/credentials")); open.Location = new Point(36, 178); p.Controls.Add(open);
        var api = Button("Open Calendar API", (_, _) => Open("https://console.cloud.google.com/apis/library/calendar-json.googleapis.com")); api.Location = new Point(220, 178); p.Controls.Add(api);
        var select = Button("Select credentials JSON", SelectGoogle); select.Location = new Point(36, 225); p.Controls.Add(select);
        Button connect = null!;
        connect = Button("Connect Google Calendar", (_, _) => {
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
        var p = Page("Local coding agents");
        p.Controls.Add(new Label { Text = "The bridge reuses each CLI's existing login. npm installs are detected from %APPDATA%\\npm, %LOCALAPPDATA%\\npm, .npm-global and `npm prefix -g` — no reinstall needed.", AutoSize = false, Width = 700, Height = 24, Location = new Point(36, 64), Font = new Font("Segoe UI", 8.5f) });
        var buttonSets = new[]
        {
            new[] { "Rescan", "Test", "Login", "Locate manually" },
            new[] { "Install", "Rescan", "Test", "Login", "Locate manually" },
            new[] { "Install", "Rescan", "Test", "Login", "Use Gemini API", "Locate manually" },
        };
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var y = 104 + i * 46;
            agentStatusLabels[index] = new Label { AutoSize = false, Width = 172, Height = 30, Location = new Point(36, y + 3), Font = new Font("Segoe UI", 8.5f), Text = "Checking...", AutoEllipsis = true };
            p.Controls.Add(agentStatusLabels[index]);
            p.Controls.Add(new Label { Text = AgentTitles[i], AutoSize = true, Location = new Point(36, y - 12), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) });
            var x = 214;
            foreach (var buttonText in buttonSets[i])
            {
                var b = Button(buttonText, (_, _) => AgentAction(index, buttonText));
                b.Font = new Font("Segoe UI", 8.5f);
                b.AutoSize = false;
                b.Size = new Size(ProviderButtonWidth(buttonText), 30);
                b.Location = new Point(x, y);
                p.Controls.Add(b);
                x += b.Width + 6;
            }
        }
        p.Controls.Add(new Label { Text = "Optional Gemini API mode (stored with Windows DPAPI) — lets Gemini work even when the CLI cannot log in:", AutoSize = true, Location = new Point(38, 252), Font = new Font("Segoe UI", 8.5f) });
        geminiKeyBox = new TextBox { Width = 300, UseSystemPasswordChar = true, Location = new Point(38, 278) }; p.Controls.Add(geminiKeyBox);
        var saveKey = Button("Save Gemini key", (_, _) => { if (!string.IsNullOrWhiteSpace(geminiKeyBox.Text)) { SecretStore.SaveGemini(geminiKeyBox.Text.Trim()); geminiKeyBox.Clear(); status.Text = "✓ Gemini API key encrypted for this Windows user."; RefreshAgentRow(2, force: true); } }); saveKey.Font = new Font("Segoe UI", 8.5f); saveKey.Location = new Point(345, 276); p.Controls.Add(saveKey);
        var openAi = Button("Open Google AI Studio", (_, _) => Open("https://aistudio.google.com/apikey")); openAi.Font = new Font("Segoe UI", 8.5f); openAi.Location = new Point(480, 276); p.Controls.Add(openAi);
        p.Controls.Add(startup); startup.Location = new Point(38, 322);
        var back = Button("Back", (_, _) => pages.SelectedIndex = 2); back.Location = new Point(36, 364); p.Controls.Add(back); var next = Button("Finish", (_, _) => { SetupState.Save(true, startup.Checked); ConfigureStartup(startup.Checked); pages.SelectedIndex = 4; }); next.Location = new Point(110, 364); p.Controls.Add(next); return p;
    }
    TabPage FinishPage()
    {
        var p = Page("Dynamic Island is ready"); p.Controls.Add(new Label { Text = "The bridge can now be started automatically and configured again later from Setup & Integrations.", AutoSize = true, Location = new Point(36, 90), Font = new Font("Segoe UI", 11) }); var done = Button("Finish Anyway", (_, _) => Close()); done.Location = new Point(36, 300); p.Controls.Add(done); return p;
    }

    // --- Provider row actions ------------------------------------------------

    static int ProviderButtonWidth(string label) => label switch
    {
        "Test" => 44,
        "Login" => 52,
        "Rescan" => 62,
        "Install" => 60,
        "Use Gemini API" => 104,
        "Locate manually" => 106,
        _ => 80,
    };

    void AgentAction(int index, string action)
    {
        var agent = AgentNames[index];
        switch (action)
        {
            case "Rescan":
                RefreshAgentRow(index, force: true);
                break;
            case "Test":
                status.Text = "Running harmless " + AgentTitles[index] + " test (--version)...";
                RefreshAgentRow(index, force: true);
                break;
            case "Login":
                LaunchLogin(index);
                break;
            case "Locate manually":
                LocateManually(index);
                break;
            case "Install":
                InstallAgent(index);
                break;
            case "Use Gemini API":
                geminiKeyBox.Focus();
                status.Text = "Paste a Gemini API key (aistudio.google.com/apikey), then press Save Gemini key. The API key never leaves this PC except to Google's API.";
                break;
        }
    }

    void RefreshAgentRow(int index, bool force)
    {
        var agent = AgentNames[index];
        agentStatusLabels[index].Text = "Checking...";
        _ = Task.Run(() =>
        {
            var result = AgentReadiness.Check(agent, force);
            BeginInvoke(() =>
            {
                var text = AgentReadiness.FormatState(result);
                if (result.Path is not null && result.State != AgentReadiness.Ready) text += "  " + result.Path;
                agentStatusLabels[index].Text = text;
                if (result.State == AgentReadiness.Error) status.Text = result.Detail;
            });
        });
    }

    void LaunchLogin(int index)
    {
        var agent = AgentNames[index];
        var path = CliDiscovery.Find(agent);
        if (path is null)
        {
            status.Text = agent + " is not installed yet. Use Install (or Locate manually) first.";
            return;
        }
        if (MessageBox.Show(this,
            "Open a console where " + AgentTitles[index] + " can sign you in?\n\nComplete the sign-in in the console window, close it, then press Rescan.",
            "Sign in to " + AgentTitles[index], MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            ProcessStartInfo psi;
            if (extension is ".cmd" or ".bat")
            {
                psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { UseShellExecute = true };
                psi.ArgumentList.Add("/k");
                psi.ArgumentList.Add(path);
            }
            else if (extension == ".ps1")
            {
                psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true };
                foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path }) psi.ArgumentList.Add(a);
            }
            else
            {
                psi = new ProcessStartInfo(path) { UseShellExecute = true };
            }
            Process.Start(psi);
            status.Text = "Console opened — finish the " + AgentTitles[index] + " sign-in, then press Rescan.";
        }
        catch (Exception ex) { status.Text = "Could not open the login console: " + ex.Message; }
    }

    void InstallAgent(int index)
    {
        var package = index == 1 ? "@openai/codex" : "@google/gemini-cli";
        if (MessageBox.Show(this,
            "Install " + AgentTitles[index] + " with npm?\n\nThis will run:\n    npm install -g " + package + "\n\nDo not close this window while the install is running.",
            "Install " + AgentTitles[index], MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        var npm = CliDiscovery.Find("npm");
        if (npm is null)
        {
            status.Text = "npm was not found. Install Node.js from nodejs.org, then press Rescan.";
            return;
        }
        status.Text = "Installing " + AgentTitles[index] + " with npm... this can take a few minutes.";
        _ = Task.Run(() =>
        {
            var run = ProcessLauncher.Run(npm, new[] { "install", "-g", package }, 15 * 60 * 1000);
            var ok = run is not null && run.Value.Code == 0;
            var detail = run is null ? "unknown error"
                : run.Value.Error.Length > 0 ? CliDiscovery.FirstLine(run.Value.Error)
                : CliDiscovery.FirstLine(run.Value.Output);
            BeginInvoke(() =>
            {
                status.Text = ok ? "✓ " + AgentTitles[index] + " installed — rescanning..." : "Install " + AgentTitles[index] + " failed: " + detail;
                RefreshAgentRow(index, force: true); // automatically rescan after install
                RefreshChecks();
            });
        });
    }

    void LocateManually(int index)
    {
        var agent = AgentNames[index];
        using var dialog = new OpenFileDialog { Filter = "Executables|*.exe;*.cmd;*.bat;*.ps1|All files|*.*", Title = "Select the " + agent + " executable" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        CliDiscovery.SaveManualPath(agent, dialog.FileName);
        status.Text = "✓ Manual executable saved for " + agent + ": " + dialog.FileName;
        RefreshAgentRow(index, force: true);
    }

    static void ConfigureStartup(bool enabled)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true); if (key is null) return; if (enabled) key.SetValue("DynamicIslandBridge", "\"" + Application.ExecutablePath + "\""); else key.DeleteValue("DynamicIslandBridge", false); } catch { }
    }
    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}
