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
    // Wizard rows map 1:1 to bridge agents. The Google individual coding-agent
    // row is Antigravity (agy) — the Gemini CLI individual login is obsolete
    // for personal accounts and is kept only as a clearly labeled legacy
    // option for enterprise/Cloud Code Assist users.
    static readonly string[] RowAgents = { "claude", "codex", "antigravity", "gemini" };
    static readonly string[] RowTitles = { "Claude Code", "Codex CLI", "Google \u00b7 Antigravity (recommended)", "Gemini CLI \u2014 legacy (enterprise/Cloud)" };

    readonly TabControl pages = new() { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
    readonly Label status = new() { AutoSize = false, Height = 28, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
    readonly CheckBox startup = new() { Text = "Start Dynamic Island Bridge automatically with Windows", AutoSize = true, Checked = true };
    readonly Label[] checks = new Label[8];
    readonly Label[] agentStatusLabels = new Label[4];
    TextBox geminiKeyBox = null!;

    public SetupWizard()
    {
        Text = "Dynamic Island v2 Setup"; Width = 780; Height = 600; MinimumSize = new Size(680, 520); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        pages.TabPages.Add(WelcomePage()); pages.TabPages.Add(CheckPage()); pages.TabPages.Add(GooglePage()); pages.TabPages.Add(AgentsPage()); pages.TabPages.Add(FinishPage());
        Controls.Add(pages); Controls.Add(status); pages.SelectedIndexChanged += (_, _) => { if (pages.SelectedIndex == 1) RefreshChecks(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        for (var i = 0; i < RowAgents.Length; i++) RefreshAgentRow(i, force: false);
    }

    static Label Heading(string text) => new() { Text = text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(28, 24) };
    static Button Button(string text, EventHandler action) { var b = new Button { Text = text, AutoSize = true, Height = 30 }; b.Click += action; return b; }
    TabPage Page(string title) { var p = new TabPage(title) { Padding = new Padding(28) }; p.Controls.Add(Heading(title)); return p; }
    TabPage WelcomePage()
    {
        var p = Page("Welcome"); p.Controls.Add(new Label { Text = "Dynamic Island v2\n\nA simple setup for Google Calendar, Claude Code, Codex, Google Antigravity, Gemini API, and startup behavior.", AutoSize = true, Location = new Point(32, 88), Font = new Font("Segoe UI", 11) });
        var list = new Label { Text = "\u2713 Google Calendar\n\u2713 Claude Code\n\u2713 OpenAI Codex\n\u2713 Google Antigravity\n\u2713 Gemini API fallback\n\u2713 Startup behavior", AutoSize = true, Location = new Point(36, 190), Font = new Font("Segoe UI", 10) }; p.Controls.Add(list);
        var start = Button("Start Setup", (_, _) => pages.SelectedIndex = 1); start.Location = new Point(36, 320); p.Controls.Add(start); var skip = Button("Skip For Now", (_, _) => { SetupState.Save(false, false); Close(); }); skip.Location = new Point(150, 320); p.Controls.Add(skip); return p;
    }
    TabPage CheckPage()
    {
        var p = Page("System check"); for (int i = 0; i < checks.Length; i++) { checks[i] = new Label { AutoSize = false, Width = 700, Height = 36, Location = new Point(36, 66 + i * 36), Font = new Font("Segoe UI", 9) }; p.Controls.Add(checks[i]); }
        var windhawk = Button("Open Windhawk", (_, _) => Open("https://windhawk.net/")); windhawk.Location = new Point(190, 380); p.Controls.Add(windhawk); var copySource = Button("Copy mod source", (_, _) => { try { var path = Path.Combine(AppContext.BaseDirectory, "sourcecode"); Clipboard.SetText(File.ReadAllText(path)); status.Text = "\u2713 Windhawk source copied."; } catch { status.Text = "Source file is not included in this package."; } }); copySource.Location = new Point(305, 380); p.Controls.Add(copySource); var test = Button("Test All", (_, _) => { status.Text = "Running provider tests (this executes each CLI's real harmless check)..."; RefreshChecks(); for (var i = 0; i < RowAgents.Length; i++) RefreshAgentRow(i, force: true); }); test.Location = new Point(430, 380); p.Controls.Add(test); var next = Button("Next", (_, _) => pages.SelectedIndex = 2); next.Location = new Point(36, 380); p.Controls.Add(next); var back = Button("Back", (_, _) => pages.SelectedIndex = 0); back.Location = new Point(110, 380); p.Controls.Add(back); return p;
    }
    void RefreshChecks()
    {
        for (var i = 0; i < checks.Length; i++) checks[i].Text = "Checking...";
        _ = Task.Run(() =>
        {
            var claude = AgentReadiness.Check("claude");
            var codex = AgentReadiness.Check("codex");
            var google = AgentReadiness.Check("google");
            var git = CliDiscovery.Find("git");
            BeginInvoke(() => ApplyChecks(claude, codex, google, git));
        });
    }
    void ApplyChecks(AgentReadiness.AgentResult claude, AgentReadiness.AgentResult codex, AgentReadiness.AgentResult google, string? git)
    {
        string Describe(AgentReadiness.AgentResult r, bool apiFallback)
        {
            if (r.Path is null) return apiFallback && r.State == AgentReadiness.Ready ? "Ready via Gemini API (no CLI needed)" : "Not installed";
            var mark = r.State == AgentReadiness.Ready ? "\u2713 " : "";
            return mark + AgentReadiness.FormatState(r) + "  " + r.Path;
        }
        var rows = new (string, string)[]
        {
            ("Windows", Environment.OSVersion.VersionString),
            (".NET runtime", Environment.Version.ToString()),
            ("Windhawk", "Install/enable the source mod"),
            ("Claude Code", Describe(claude, false)),
            ("Codex CLI", Describe(codex, false)),
            ("Google (Antigravity/API)", Describe(google, true)),
            ("Git", git is null ? "Not installed" : "\u2713 Installed  " + git),
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
            _ = Task.Run(async () => { try { await Program.ConnectGoogleCalendar(); BeginInvoke(() => { status.Text = "\u2713 Google Calendar connected."; connect.Enabled = true; }); } catch (Exception ex) { BeginInvoke(() => { status.Text = "Calendar connection failed: " + ex.Message; connect.Enabled = true; }); } });
        }); connect.Location = new Point(210, 225); p.Controls.Add(connect);
        var back = Button("Back", (_, _) => pages.SelectedIndex = 1); back.Location = new Point(36, 310); p.Controls.Add(back); var next = Button("Next", (_, _) => pages.SelectedIndex = 3); next.Location = new Point(110, 310); p.Controls.Add(next); return p;
    }
    void SelectGoogle(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Google OAuth JSON|*.json", Title = "Select downloaded Google Desktop OAuth credentials" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { using var doc = JsonDocument.Parse(File.ReadAllText(dialog.FileName)); var root = doc.RootElement.TryGetProperty("installed", out var installed) ? installed : doc.RootElement.GetProperty("web"); var id = root.GetProperty("client_id").GetString(); var secret = root.GetProperty("client_secret").GetString(); if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw new InvalidDataException(); CredentialStore.SaveGoogle(new GoogleDesktopCredentials(id, secret)); status.Text = "\u2713 OAuth client saved with Windows DPAPI."; }
        catch { status.Text = "That file is not a valid Google Desktop OAuth JSON file."; }
    }
    TabPage AgentsPage()
    {
        var p = Page("Local coding agents");
        p.Controls.Add(new Label { Text = "Antigravity is Google's current coding agent for individual accounts (Gemini CLI individual login is obsolete). npm-installed CLIs are detected in place from the npm folders, `npm prefix -g` and the Antigravity install folder — no reinstall needed.", AutoSize = false, Width = 704, Height = 34, Location = new Point(36, 58), Font = new Font("Segoe UI", 8.5f) });
        var buttonSets = new[]
        {
            new[] { "Rescan", "Test", "Login", "Locate manually" },
            new[] { "Install", "Rescan", "Test", "Login", "Locate manually" },
            new[] { "Install", "Rescan", "Test", "Login", "Locate manually" },
            new[] { "Install", "Rescan", "Test", "Login", "Locate manually" },
        };
        for (var i = 0; i < RowAgents.Length; i++)
        {
            var index = i;
            var y = 100 + i * 46;
            var title = new Label { Text = RowTitles[i], AutoSize = true, Location = new Point(36, y - 13), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            if (i == 3) title.ForeColor = SystemColors.GrayText; // de-emphasized legacy row
            p.Controls.Add(title);
            agentStatusLabels[index] = new Label { AutoSize = false, Width = 172, Height = 30, Location = new Point(36, y + 3), Font = new Font("Segoe UI", 8.5f), Text = "Checking...", AutoEllipsis = true };
            p.Controls.Add(agentStatusLabels[index]);
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
        p.Controls.Add(new Label { Text = "Optional Gemini API mode (stored with Windows DPAPI) — works without any CLI and is the fallback when Antigravity is unavailable:", AutoSize = true, Location = new Point(38, 286), Font = new Font("Segoe UI", 8.5f) });
        geminiKeyBox = new TextBox { Width = 260, UseSystemPasswordChar = true, Location = new Point(38, 312) }; p.Controls.Add(geminiKeyBox);
        var saveKey = Button("Save Gemini key", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(geminiKeyBox.Text)) return;
            SecretStore.SaveGemini(geminiKeyBox.Text.Trim());
            geminiKeyBox.Clear();
            status.Text = "\u2713 Gemini API key encrypted for this Windows user. Testing the key...";
            _ = Task.Run(() =>
            {
                var ok = GeminiApi.TestSynchronous();
                BeginInvoke(() => status.Text = ok ? "\u2713 Gemini API key works." : "Key saved, but the API test failed — check the key and your network.");
            });
        }); saveKey.Font = new Font("Segoe UI", 8.5f); saveKey.AutoSize = false; saveKey.Size = new Size(ProviderButtonWidth("Save Gemini key"), 30); saveKey.Location = new Point(304, 310); p.Controls.Add(saveKey);
        var openAi = Button("Open Google AI Studio", (_, _) => Open("https://aistudio.google.com/apikey")); openAi.Font = new Font("Segoe UI", 8.5f); openAi.AutoSize = false; openAi.Size = new Size(ProviderButtonWidth("Open Google AI Studio"), 30); openAi.Location = new Point(414, 310); p.Controls.Add(openAi);
        var testApi = Button("Test", (_, _) =>
        {
            if (!GeminiApi.HasKey()) { status.Text = "Save a Gemini API key first."; return; }
            status.Text = "Testing Gemini API key...";
            _ = Task.Run(() =>
            {
                var ok = GeminiApi.TestSynchronous();
                BeginInvoke(() => status.Text = ok ? "\u2713 Gemini API key works." : "Gemini API test failed — check the key and your network.");
            });
        }); testApi.Font = new Font("Segoe UI", 8.5f); testApi.AutoSize = false; testApi.Size = new Size(ProviderButtonWidth("Test"), 30); testApi.Location = new Point(560, 310); p.Controls.Add(testApi);
        p.Controls.Add(startup); startup.Location = new Point(38, 352);
        var back = Button("Back", (_, _) => pages.SelectedIndex = 2); back.Location = new Point(36, 392); p.Controls.Add(back); var next = Button("Finish", (_, _) => { SetupState.Save(true, startup.Checked); ConfigureStartup(startup.Checked); pages.SelectedIndex = 4; }); next.Location = new Point(110, 392); p.Controls.Add(next); return p;
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
        "Save Gemini key" => 104,
        "Open Google AI Studio" => 140,
        "Use Gemini API" => 104,
        "Locate manually" => 106,
        _ => 80,
    };

    void AgentAction(int index, string action)
    {
        switch (action)
        {
            case "Rescan":
                RefreshAgentRow(index, force: true);
                break;
            case "Test":
                status.Text = "Running harmless " + RowTitles[index] + " test...";
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
        var agent = RowAgents[index];
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
        var agent = RowAgents[index];
        var path = CliDiscovery.Find(agent == "antigravity" ? "agy" : agent);
        if (path is null)
        {
            status.Text = RowTitles[index] + " is not installed yet. Use Install (or Locate manually) first.";
            return;
        }
        if (MessageBox.Show(this,
            "Open a console where " + RowTitles[index] + " can sign you in?\n\nComplete the sign-in in the console window, close it, then press Rescan.",
            "Sign in to " + RowTitles[index], MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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
                // Native binaries (agy.exe, claude.exe, ...) open their own
                // console for the interactive sign-in wizard.
                psi = new ProcessStartInfo(path) { UseShellExecute = true };
            }
            Process.Start(psi);
            status.Text = "Console opened — finish the " + RowTitles[index] + " sign-in, then press Rescan.";
        }
        catch (Exception ex) { status.Text = "Could not open the login console: " + ex.Message; }
    }

    void InstallAgent(int index)
    {
        var agent = RowAgents[index];
        string commandText;
        Action install;
        if (agent == "antigravity")
        {
            // Official Antigravity CLI installer (antigravity.google/docs/cli/install).
            commandText = "irm https://antigravity.google/cli/install.ps1 | iex";
            install = () =>
            {
                var run = ProcessLauncher.Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", commandText }, 10 * 60 * 1000);
                var ok = run is not null && run.Value.Code == 0;
                var detail = run is null ? "unknown error"
                    : run.Value.Error.Length > 0 ? CliDiscovery.FirstLine(run.Value.Error)
                    : CliDiscovery.FirstLine(run.Value.Output);
                BeginInvoke(() =>
                {
                    status.Text = ok ? "\u2713 Antigravity CLI installed — rescanning..." : "Antigravity install failed: " + detail;
                    RefreshAgentRow(index, force: true); // automatically rescan after install
                    RefreshChecks();
                });
            };
        }
        else if (agent == "codex")
        {
            commandText = "npm install -g @openai/codex";
            install = () => RunNpmInstall(index, "@openai/codex");
        }
        else
        {
            commandText = "npm install -g @google/gemini-cli";
            install = () => RunNpmInstall(index, "@google/gemini-cli");
        }
        if (MessageBox.Show(this,
            "Install " + RowTitles[index] + "?\n\nThis will run:\n    " + commandText + "\n\nDo not close this window while the install is running.",
            "Install " + RowTitles[index], MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        if (agent is "codex" or "gemini" && CliDiscovery.Find("npm") is null)
        {
            status.Text = "npm was not found. Install Node.js from nodejs.org, then press Rescan.";
            return;
        }
        status.Text = "Installing " + RowTitles[index] + "... this can take a few minutes.";
        _ = Task.Run(install);
    }

    void RunNpmInstall(int index, string package)
    {
        var npm = CliDiscovery.Find("npm");
        if (npm is null)
        {
            BeginInvoke(() => status.Text = "npm was not found. Install Node.js from nodejs.org, then press Rescan.");
            return;
        }
        var run = ProcessLauncher.Run(npm, new[] { "install", "-g", package }, 15 * 60 * 1000);
        var ok = run is not null && run.Value.Code == 0;
        var detail = run is null ? "unknown error"
            : run.Value.Error.Length > 0 ? CliDiscovery.FirstLine(run.Value.Error)
            : CliDiscovery.FirstLine(run.Value.Output);
        BeginInvoke(() =>
        {
            status.Text = ok ? "\u2713 " + RowTitles[index] + " installed — rescanning..." : "Install " + RowTitles[index] + " failed: " + detail;
            RefreshAgentRow(index, force: true); // automatically rescan after install
            RefreshChecks();
        });
    }

    void LocateManually(int index)
    {
        var agent = RowAgents[index];
        using var dialog = new OpenFileDialog { Filter = "Executables|*.exe;*.cmd;*.bat;*.ps1|All files|*.*", Title = "Select the " + RowTitles[index] + " executable" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // The bridge discovers by binary name; the legacy gemini CLI is looked
        // up as "gemini", Antigravity as "agy".
        var storeName = agent == "antigravity" ? "agy" : agent;
        CliDiscovery.SaveManualPath(storeName, dialog.FileName);
        status.Text = "\u2713 Manual executable saved for " + RowTitles[index] + ": " + dialog.FileName;
        RefreshAgentRow(index, force: true);
    }

    static void ConfigureStartup(bool enabled)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true); if (key is null) return; if (enabled) key.SetValue("DynamicIslandBridge", "\"" + Application.ExecutablePath + "\""); else key.DeleteValue("DynamicIslandBridge", false); } catch { }
    }
    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}
