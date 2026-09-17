using System.Diagnostics;
using System.Text;
using System.Text.Json;

// ---------------------------------------------------------------------------
// Windows-first CLI discovery for npm-installed tools (Claude Code, Codex,
// Gemini CLI) and any other executable the bridge can launch.
//
// Search order per candidate name:
//   1. Manual override picked in the setup wizard (cli-overrides.json)
//   2. Inherited PATH entries
//   3. %APPDATA%\npm
//   4. %LOCALAPPDATA%\npm
//   5. %USERPROFILE%\AppData\Roaming\npm
//   6. %USERPROFILE%\.npm-global
//   7. %LOCALAPPDATA%\Programs
//   8. `npm prefix -g`  (+ <prefix>\node_modules)
//   9. `npm root -g`
//  10. where.exe fallback
//
// Each directory is probed in this extension order: .exe, .cmd, .bat, .ps1,
// then extensionless names. Users who installed Codex/Gemini with npm do NOT
// need to reinstall them: the npm shim folders above cover every default
// npm global prefix on Windows.
// ---------------------------------------------------------------------------
internal static class CliDiscovery
{
    static readonly string[] Extensions = { ".exe", ".cmd", ".bat", ".ps1", "" };
    static Dictionary<string, string>? manualPaths;
    static List<string>? cachedDirs;
    static DateTimeOffset cachedDirsAt = DateTimeOffset.MinValue;
    static readonly object CacheLock = new();

    // --- Manual executable selection (persisted per Windows user) ----------

    static Dictionary<string, string> ManualPaths()
    {
        lock (CacheLock)
        {
            if (manualPaths is null)
            {
                var path = Path.Combine(SetupState.Root, "cli-overrides.json");
                try
                {
                    if (File.Exists(path)) manualPaths = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(StringComparer.OrdinalIgnoreCase);
                }
                catch { /* fall through to empty map */ }
                manualPaths ??= new(StringComparer.OrdinalIgnoreCase);
            }
            return manualPaths;
        }
    }

    internal static string? GetManualPath(string name) => ManualPaths().TryGetValue(name, out var value) ? value : null;

    internal static void SaveManualPath(string name, string path)
    {
        var map = ManualPaths();
        map[name] = path;
        PersistManual(map);
        InvalidateCache();
    }

    internal static void ClearManualPath(string name)
    {
        var map = ManualPaths();
        map.Remove(name);
        PersistManual(map);
        InvalidateCache();
    }

    static void PersistManual(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(SetupState.Root);
            File.WriteAllText(Path.Combine(SetupState.Root, "cli-overrides.json"), JsonSerializer.Serialize(map));
        }
        catch { /* manual paths are best-effort */ }
    }

    internal static void InvalidateCache()
    {
        lock (CacheLock)
        {
            cachedDirs = null;
            cachedDirsAt = DateTimeOffset.MinValue;
        }
    }

    // --- Directory collection ----------------------------------------------

    internal static List<string> GetSearchDirectories()
    {
        lock (CacheLock)
        {
            if (cachedDirs is not null && DateTimeOffset.UtcNow - cachedDirsAt < TimeSpan.FromSeconds(60)) return cachedDirs;
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);        // %APPDATA%
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); // %LOCALAPPDATA%
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(appData, "npm"));
        dirs.Add(Path.Combine(localAppData, "npm"));
        dirs.Add(Path.Combine(profile, "AppData", "Roaming", "npm"));
        dirs.Add(Path.Combine(profile, ".npm-global"));
        dirs.Add(Path.Combine(localAppData, "Programs"));
        // Antigravity CLI (agy): official Windows install location is
        // %LOCALAPPDATA%\agy\bin\agy.exe (antigravity.google/docs/cli/install).
        // Explicit probing here means a fresh install is found without waiting
        // for the user PATH to refresh.
        dirs.Add(Path.Combine(localAppData, "agy", "bin"));
        dirs.Add(Path.Combine(localAppData, "Antigravity", "bin"));
        dirs.Add(Path.Combine(localAppData, "Antigravity"));
        foreach (var npmDir in NpmGlobalDirs())
        {
            dirs.Add(npmDir);
            dirs.Add(Path.Combine(npmDir, "node_modules"));
        }
        var distinct = dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (CacheLock)
        {
            cachedDirs = distinct;
            cachedDirsAt = DateTimeOffset.UtcNow;
        }
        return distinct;
    }

    // Runs `npm prefix -g` and `npm root -g` (when npm itself can be found) to
    // cover custom npm prefixes such as a portable Node.js or nvm4w install.
    static List<string> NpmGlobalDirs()
    {
        var dirs = new List<string>();
        var npm = FindNpmOnPathOnly();
        if (npm is null) return dirs;
        foreach (var args in new[] { new[] { "prefix", "-g" }, new[] { "root", "-g" } })
        {
            var run = ProcessLauncher.Run(npm, args, 6000);
            if (run is null) continue;
            var line = run.Value.Output.Split('\n').FirstOrDefault()?.Trim().Trim('"').Trim();
            if (!string.IsNullOrWhiteSpace(line) && Directory.Exists(line)) dirs.Add(line);
        }
        return dirs;
    }

    static string? FindNpmOnPathOnly()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in Extensions)
            {
                try { var candidate = Path.Combine(dir, "npm" + ext); if (File.Exists(candidate)) return candidate; } catch { }
            }
        }
        return FindWithWhere("npm");
    }

    // --- where.exe fallback --------------------------------------------------

    // `where.exe <pattern>` is probed once per extension; its INFO/ERROR
    // status lines are ignored so the first real path wins.
    internal static string? FindWithWhere(string name)
    {
        foreach (var ext in Extensions)
        {
            var found = RunWhere(name + ext);
            if (found is not null) return found;
        }
        return null;
    }

    static string? RunWhere(string pattern)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(pattern);
            using var p = Process.Start(psi);
            if (p is null) return null;
            string? first = null;
            while (true)
            {
                var line = p.StandardOutput.ReadLine();
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) continue;
                if (first is null) first = line;
            }
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } }
            return first;
        }
        catch { return null; }
    }

    // --- Main entry point ----------------------------------------------------

    internal static string? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var manual = GetManualPath(name);
        if (manual is not null && SafeExists(manual)) return manual;
        foreach (var dir in GetSearchDirectories())
        {
            foreach (var ext in Extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (SafeExists(candidate)) return candidate;
            }
        }
        return FindWithWhere(name);
    }

    static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    internal static string? Version(string path)
    {
        var run = ProcessLauncher.Run(path, new[] { "--version" }, 8000);
        if (run is null) return null;
        return string.IsNullOrWhiteSpace(run.Value.Output) ? null : FirstLine(run.Value.Output);
    }

    internal static string FirstLine(string value)
    {
        var line = value.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : FirstLine2(value);
    }

    static string FirstLine2(string value)
    {
        var line = value.Split('\r').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : value.Trim();
    }
}

// ---------------------------------------------------------------------------
// Process launching that understands Windows npm shims:
//   - .cmd / .bat  -> `cmd.exe /d /s /c <script> <args...>`
//   - .ps1         -> `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <args...>`
//   - anything else-> launched directly
// Arguments are always passed through ArgumentList (never a shell string).
// ---------------------------------------------------------------------------
internal static class ProcessLauncher
{
    internal static ProcessStartInfo Build(string file, IEnumerable<string> args, string? workingDirectory = null, bool useShellExecute = false)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        string commandFile = file;
        var prefix = new List<string>();
        if (extension is ".cmd" or ".bat")
        {
            commandFile = comSpec;
            prefix.AddRange(new[] { "/d", "/s", "/c", file });
        }
        else if (extension == ".ps1")
        {
            commandFile = "powershell.exe";
            prefix.AddRange(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file });
        }
        var psi = new ProcessStartInfo(commandFile)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = !useShellExecute,
            RedirectStandardError = !useShellExecute,
            UseShellExecute = useShellExecute,
            CreateNoWindow = !useShellExecute,
        };
        foreach (var arg in prefix) psi.ArgumentList.Add(arg);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    internal static (int Code, string Output, string Error)? Run(string file, IEnumerable<string> args, int timeoutMs = 8000, string? workingDirectory = null)
    {
        try
        {
            using var p = Process.Start(Build(file, args, workingDirectory));
            if (p is null) return (-1, "", "process_start_failed");
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "", "timeout");
            }
            var stdout = (stdoutTask.IsCompleted ? stdoutTask.Result : SafeRead(stdoutTask)).Trim();
            var stderr = (stderrTask.IsCompleted ? stderrTask.Result : SafeRead(stderrTask)).Trim();
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception e) { return (-1, "", CliDiscovery.FirstLine(e.Message)); }
    }

    static string SafeRead(Task<string> task)
    {
        try { return task.GetAwaiter().GetResult(); } catch { return ""; }
    }
}

// ---------------------------------------------------------------------------
// Gemini API key helpers. The key is only ever read from DPAPI storage and
// sent as the x-goog-api-key request header. It is never logged or placed in
// a URL.
// ---------------------------------------------------------------------------
internal static class GeminiApi
{
    internal static string Model => Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GEMINI_API_MODEL") ?? "gemini-2.0-flash";

    internal static bool HasKey() => !string.IsNullOrWhiteSpace(SecretStore.LoadGemini());

    internal static async Task<bool> TestAsync()
    {
        var key = SecretStore.LoadGemini();
        if (string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", key);
            var payload = JsonSerializer.Serialize(new
            {
                contents = new object[] { new { parts = new object[] { new { text = "Reply with READY only." } } } },
            });
            using var response = await http.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // Synchronous wrapper that is safe to call from the WinForms UI thread and
    // from pipe-handler threads (no sync-context deadlock).
    internal static bool TestSynchronous()
    {
        try { return Task.Run(async () => await TestAsync().ConfigureAwait(false)).GetAwaiter().GetResult(); }
        catch { return false; }
    }
}

// ---------------------------------------------------------------------------
// CLI readiness: not_installed / auth_required / ready / error.
// The check actually executes each CLI (`--version`, a harmless provider
// test) instead of only checking that a file exists. Gemini additionally
// falls back to the Gemini API: if the stored API key passes its test,
// Gemini reports READY (provider "api") even when the Gemini CLI is not
// installed at all.
// ---------------------------------------------------------------------------
internal static class AgentReadiness
{
    internal const string NotFound = "not_installed";
    internal const string AuthRequired = "auth_required";
    internal const string Ready = "ready";
    internal const string Error = "error";

    internal sealed record AgentResult(string State, string? Path, string Detail, string? Provider)
    {
        public bool Installed => State == Ready || State == AuthRequired || State == Error;
    }

    static readonly Dictionary<string, (DateTimeOffset At, AgentResult Value)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object CacheLock = new();
    static DateTimeOffset apiCheckedAt = DateTimeOffset.MinValue;
    static bool apiTestOk = false;

    internal static AgentResult Check(string agent, bool force = false)
    {
        lock (CacheLock)
        {
            if (!force && Cache.TryGetValue(agent, out var hit) &&
                DateTimeOffset.UtcNow - hit.At < (agent is "gemini" or "antigravity" or "google" ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(45)))
            {
                return hit.Value;
            }
        }
        var value = agent switch
        {
            "gemini" => CheckCli("gemini"),        // legacy Gemini CLI
            "antigravity" => CheckAntigravity(),   // current individual path
            "google" => CheckGoogle(),             // aggregated Google slot
            _ => CheckCli(agent),
        };
        lock (CacheLock) Cache[agent] = (DateTimeOffset.UtcNow, value);
        return value;
    }

    internal static AgentResult CheckCli(string agent)
    {
        var path = CliDiscovery.Find(agent);
        if (path is null) return new AgentResult(NotFound, null, "Not installed", null);
        var run = ProcessLauncher.Run(path, new[] { "--version" }, 10000);
        if (run is null) return new AgentResult(Error, path, "Error: could not run the CLI", null);
        var output = (run.Value.Output + "\n" + run.Value.Error).ToLowerInvariant();
        if (run.Value.Code != 0 && LooksLikeAuthProblem(output))
            return new AgentResult(AuthRequired, path, "Installed \u00b7 authentication required", null);
        if (run.Value.Code != 0)
            return new AgentResult(Error, path, "Error \u00b7 " + CliDiscovery.FirstLine(run.Value.Output + "\n" + run.Value.Error), null);
        var credential = CredentialPath(agent);
        bool hasCredentials = false;
        if (credential is not null)
        {
            try { hasCredentials = Directory.Exists(credential) || File.Exists(credential); }
            catch { hasCredentials = false; }
        }
        if (!hasCredentials)
            return new AgentResult(AuthRequired, path, "Installed \u00b7 authentication required", null);
        var version = CliDiscovery.FirstLine(run.Value.Output);
        return new AgentResult(Ready, path, "Ready" + (version.Length > 0 ? " \u00b7 " + version : ""), null);
    }

    // Antigravity CLI (agy) is the current Google individual coding-agent path
    // (Gemini CLI individual login is obsolete). Readiness runs real, harmless
    // commands: `agy --version` (installed/healthy) and `agy models` (session
    // valid; an unauthenticated session reports sign-in required).
    internal static AgentResult CheckAntigravity()
    {
        var path = CliDiscovery.Find("agy");
        if (path is null) return new AgentResult(NotFound, null, "Antigravity CLI not installed", "antigravity");
        var version = ProcessLauncher.Run(path, new[] { "--version" }, 10000);
        if (version is null || version.Value.Code != 0)
            return new AgentResult(Error, path, "Error: could not run agy", "antigravity");
        var versionLine = CliDiscovery.FirstLine(version.Value.Output);
        var probe = ProcessLauncher.Run(path, new[] { "models" }, 15000);
        if (probe is not null && probe.Value.Code == 0)
            return new AgentResult(Ready, path, "Ready" + (versionLine.Length > 0 ? " \u00b7 " + versionLine : ""), "antigravity");
        var probeText = probe is null ? "" : (probe.Value.Output + "\n" + probe.Value.Error).ToLowerInvariant();
        if (LooksLikeAuthProblem(probeText))
            return new AgentResult(AuthRequired, path, "Installed \u00b7 authentication required", "antigravity");
        // `agy models` failed for another reason (no such command, network,
        // ...): conservatively report that a sign-in may still be needed.
        return new AgentResult(AuthRequired, path, "Installed \u00b7 run agy to sign in", "antigravity");
    }

    // Aggregated Google slot: Antigravity first, then the Gemini API fallback,
    // then the legacy Gemini CLI (enterprise/Cloud Code Assist).
    internal static AgentResult CheckGoogle()
    {
        var antigravity = CheckAntigravity();
        if (antigravity.State == Ready) return antigravity;
        if (CheckApi(out var apiOk) && apiOk)
            return new AgentResult(Ready, antigravity.Path, "Ready \u00b7 Gemini API", "api");
        var legacy = CheckCli("gemini");
        if (legacy.State == Ready) return new AgentResult(Ready, legacy.Path, "Ready \u00b7 legacy Gemini CLI", "cli");
        if (antigravity.State != NotFound) return antigravity;
        if (legacy.State != NotFound) return legacy;
        return antigravity; // not installed — Antigravity is the recommended path
    }

    // Cached (120 s) Gemini API key test. Returns ok and sets detail.
    internal static bool CheckApi(out bool verified)
    {
        verified = false;
        if (!GeminiApi.HasKey()) return false;
        lock (CacheLock)
        {
            if (apiTestOk && DateTimeOffset.UtcNow - apiCheckedAt < TimeSpan.FromSeconds(120)) { verified = true; return true; }
        }
        var ok = GeminiApi.TestSynchronous();
        lock (CacheLock)
        {
            apiCheckedAt = DateTimeOffset.UtcNow;
            apiTestOk = ok;
        }
        verified = ok;
        return ok;
    }

    // Known on-disk credential locations used to distinguish "installed but
    // not signed in" from "ready". Heuristic only: a missing file reports
    // auth_required and the wizard's Login button resolves it.
    static string? CredentialPath(string agent)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return agent switch
        {
            "claude" => Path.Combine(profile, ".claude", ".credentials.json"),
            "codex" => Path.Combine(profile, ".codex", "auth.json"),
            "gemini" => Path.Combine(profile, ".gemini"),
            _ => null,
        };
    }

    static bool LooksLikeAuthProblem(string output)
    {
        return output.Contains("not logged in") || output.Contains("login required") ||
               output.Contains("log in") || output.Contains("sign in") ||
               output.Contains("unauthorized") || output.Contains("authentication") ||
               output.Contains("access denied") || output.Contains("permission denied") ||
               output.Contains(" 401") || output.Contains("unauthenticated");
    }

    internal static string FormatState(AgentResult r) => r.State switch
    {
        Ready => "Ready" + (r.Provider == "api" ? " \u00b7 Gemini API" : ""),
        AuthRequired => "Installed \u00b7 authentication required",
        NotFound => "Not installed",
        _ => "Error",
    };
}
