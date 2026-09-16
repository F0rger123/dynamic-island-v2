using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

// DynamicIslandBridge: local-only broker for Calendar and installed coding agents.
// Protocol: one UTF-8 JSON object per line over \\\\.\\pipe\\DynamicIslandBridge-v2.
// No command received here is executed by a shell. Agent arguments are passed as
// ProcessStartInfo.ArgumentList, and project roots must be existing directories.
internal static class Program
{
    const string PipeName = "DynamicIslandBridge-v2";
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIslandBridge");
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, Process> Jobs = new(StringComparer.Ordinal);
    static readonly Dictionary<string, CancellationTokenSource> ApiJobs = new(StringComparer.Ordinal);
    static readonly object JobsLock = new();

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase))) SetupState.ShowAgain(); else SetupState.ShowIfFirstRun();
        while (true)
        {
            try { await ServeClient(await CreatePipe()); }
            catch (Exception e) { Console.Error.WriteLine($"bridge: {e.Message}"); }
        }
    }

    internal static Task<bool> ConnectGoogleCalendar() => Calendar.Auth();

    static async Task<NamedPipeServerStream> CreatePipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    static async Task ServeClient(NamedPipeServerStream pipe)
    {
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length > 64 * 1024) { await Send(writer, new { ok = false, error = "message_too_large" }); continue; }
            try { using var document = JsonDocument.Parse(line); await Dispatch(document.RootElement, writer); }
            catch (JsonException) { await Send(writer, new { ok = false, error = "invalid_json" }); }
            catch (Exception e) { await Send(writer, new { ok = false, error = "request_failed", detail = e.Message }); }
        }
        pipe.Dispose();
    }

    static void SafeLog(string message) { try { File.AppendAllText(Path.Combine(DataDir, "bridge.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); } catch { } }

    // Agent streams and the request/response loop share this writer; serialize
    // writes so each JSON line reaches the pipe intact even while an agent is
    // streaming and new requests arrive.
    static readonly object WriteLock = new();

    static async Task Dispatch(JsonElement r, StreamWriter w)
    {
        var op = r.TryGetProperty("op", out var o) ? o.GetString() : null;
        SafeLog(op ?? "invalid");
        switch (op)
        {
            case "status": await Send(w, StatusPayload()); break;
            case "diagnostics":
            {
                var claude = AgentReadiness.Check("claude"); var codex = AgentReadiness.Check("codex"); var gemini = AgentReadiness.Check("gemini");
                await Send(w, new
                {
                    ok = true, bridge = "online", version = "2.0.0", runtime = Environment.Version.ToString(),
                    claude = FindOrNull("claude"), codex = FindOrNull("codex"), gemini = FindOrNull("gemini"),
                    git = FindOrNull("git"), npm = FindOrNull("npm"), setup = SetupState.IsComplete,
                    agent_state_claude = claude.State, agent_state_codex = codex.State, agent_state_gemini = gemini.State,
                    agent_provider_gemini = gemini.Provider ?? "none",
                });
                break;
            }
            case "calendar.auth": await Send(w, new { ok = await Calendar.Auth(), state = "authorized" }); break;
            case "calendar.today": await Send(w, new { ok = true, events = await Calendar.Today() }); break;
            case "gemini.api.test":
            {
                if (!GeminiApi.HasKey()) { await Send(w, new { ok = false, error = "gemini_api_key_required", provider = "gemini-api" }); break; }
                var ok = await GeminiApi.TestAsync();
                await Send(w, new { ok, provider = "gemini-api" });
                break;
            }
            case "agent.start": await StartAgent(r, w); break;
            case "agent.cancel": await Cancel(r, w); break;
            case "agent.resume": await ResumeAgent(r, w); break;
            default: await Send(w, new { ok = false, error = "unknown_operation" }); break;
        }
    }

    // Per-agent readiness is reported to the island as flat keys
    // (agent_state_<agent>) so the C++ client can parse them without a JSON
    // library. "ready" also covers the Gemini API fallback provider.
    static Dictionary<string, object?> StatusPayload()
    {
        var claude = AgentReadiness.Check("claude");
        var codex = AgentReadiness.Check("codex");
        var gemini = AgentReadiness.Check("gemini");
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["bridge"] = "online",
            ["version"] = "2.0.0",
            ["agents"] = new Dictionary<string, bool>
            {
                ["claude"] = claude.State != AgentReadiness.NotFound,
                ["codex"] = codex.State != AgentReadiness.NotFound,
                ["gemini"] = gemini.State != AgentReadiness.NotFound,
            },
            ["agent_state_claude"] = claude.State,
            ["agent_state_codex"] = codex.State,
            ["agent_state_gemini"] = gemini.State,
            ["agent_provider_gemini"] = gemini.Provider ?? "none",
        };
    }

    static string? FindOrNull(string name) { try { return CliDiscovery.Find(name); } catch { return null; } }

    static ProcessStartInfo CreateStartInfo(string file, string root, IEnumerable<string> args)
        => ProcessLauncher.Build(file, args, root);

    static async Task StartAgent(JsonElement r, StreamWriter w)
    {
        var agent = Required(r, "agent").ToLowerInvariant();
        var root = FullDirectory(Required(r, "project"));
        var prompt = Required(r, "prompt");
        if (prompt.Length > 32_000) throw new InvalidDataException("prompt_too_large");
        var provider = r.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString()?.ToLowerInvariant() : null;

        if (agent == "gemini" && provider == "api")
        {
            if (!GeminiApi.HasKey()) throw new InvalidDataException("gemini_api_key_required");
            var id = Guid.NewGuid().ToString("N");
            var cts = new CancellationTokenSource();
            lock (JobsLock) ApiJobs[id] = cts;
            await Send(w, new { ok = true, id, agent, project = root, provider = "api" });
            _ = RunGeminiApiJob(id, root, prompt, w, cts);
            return;
        }
        if (provider is not null) throw new InvalidDataException("provider_not_supported");
        if (agent is not ("claude" or "codex" or "gemini")) throw new InvalidDataException("unsupported_agent");

        var (file, args) = AgentCommand(agent, prompt);
        var psi = CreateStartInfo(file, root, args);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!p.Start()) throw new InvalidOperationException("agent_start_failed");
        var id = Guid.NewGuid().ToString("N"); lock (JobsLock) Jobs[id] = p;
        await Send(w, new { ok = true, id, agent, project = root });
        _ = StreamOutput(id, p, w, agent);
    }

    static async Task RunGeminiApiJob(string id, string root, string prompt, StreamWriter w, CancellationTokenSource cts)
    {
        var outcome = "failed";
        try { outcome = await GeminiApiAgent.Run(id, root, prompt, w, cts.Token); }
        catch { outcome = "failed"; }
        try { await Send(w, new { type = "exit", id, code = outcome == "failed" ? 1 : 0, provider = "api" }); } catch { }
        lock (JobsLock) ApiJobs.Remove(id);
        cts.Dispose();
    }

    static object NormalizeEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
            var kind = r.TryGetProperty("type", out var t) ? t.GetString() : null;
            var text = r.TryGetProperty("message", out var m) ? m.ToString() : line;
            var phase = kind switch
            {
                "tool_use" or "function_call" => "running_tool",
                "tool_result" or "function_return" => "tool_result",
                "assistant" or "message" => "working",
                "result" or "completed" => "completed",
                "error" => "failed",
                _ => "working"
            };
            return new { phase, text, raw = line };
        }
        catch { return new { phase = "working", text = line, raw = line }; }
    }

    static readonly string[] GeminiLoginErrors =
    {
        "not logged in", "login required", "log in", "sign in", "authentication",
        "unauthorized", "unauthenticated", "access denied", "permission denied",
    };

    static bool LooksLikeGeminiLoginError(string line)
    {
        var lower = line.ToLowerInvariant();
        foreach (var needle in GeminiLoginErrors)
        {
            if (lower.Contains(needle)) return true;
        }
        return lower.Contains(" 401") || lower.Contains("\"401\"");
    }

    static async Task StreamOutput(string id, Process p, StreamWriter w, string agent)
    {
        var authFailed = false;
        async Task Pump(StreamReader s, string stream)
        {
            string? line;
            while ((line = await s.ReadLineAsync()) != null)
            {
                if (agent == "gemini" && LooksLikeGeminiLoginError(line)) authFailed = true;
                try { await Send(w, new { type = "agent", id, stream, normalized = NormalizeEvent(line), data = line }); }
                catch { break; }
            }
        }
        await Task.WhenAll(Pump(p.StandardOutput, "stdout"), Pump(p.StandardError, "stderr"));
        await p.WaitForExitAsync();
        if (agent == "gemini" && authFailed && p.ExitCode != 0)
        {
            try { await Send(w, new { type = "gemini_login_unavailable", id, agent = "gemini" }); } catch { }
        }
        try { await Send(w, new { type = "exit", id, code = p.ExitCode }); } catch { }
        lock (JobsLock) Jobs.Remove(id);
        p.Dispose();
    }

    static async Task Cancel(JsonElement r, StreamWriter w)
    {
        var id = Required(r, "id");
        CancellationTokenSource? apiJob = null;
        lock (JobsLock) ApiJobs.TryGetValue(id, out apiJob);
        if (apiJob is not null)
        {
            try { apiJob.Cancel(); } catch { }
            await Send(w, new { ok = true, id, state = "cancelling" });
            return;
        }
        lock (JobsLock)
        {
            if (Jobs.TryGetValue(id, out var p))
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
            }
        }
        await Send(w, new { ok = true, id, state = "cancelling" });
    }

    static async Task ResumeAgent(JsonElement r, StreamWriter w)
    {
        var agent = Required(r, "agent").ToLowerInvariant(); var session = Required(r, "session"); var root = FullDirectory(Required(r, "project")); var prompt = r.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "Continue" : "Continue";
        var file = Find(agent); string[] args = agent switch
        {
            "claude" => new[] { "-p", prompt, "--resume", session, "--output-format", "stream-json", "--verbose" },
            "codex" => new[] { "exec", "resume", session, prompt, "--json" },
            "gemini" => new[] { "--resume", session, "-p", prompt, "--output-format", "stream-json" },
            _ => throw new InvalidDataException("unsupported_agent")
        };
        var psi = CreateStartInfo(file, root, args);
        var process = new Process { StartInfo = psi }; if (!process.Start()) throw new InvalidOperationException("agent_resume_failed");
        var id = Guid.NewGuid().ToString("N"); lock (JobsLock) Jobs[id] = process; await Send(w, new { ok = true, id, agent, resumed = session }); _ = StreamOutput(id, process, w, agent);
    }

    static (string, string[]) AgentCommand(string agent, string prompt) => agent switch
    {
        "claude" => (Find("claude"), new[] { "-p", prompt, "--output-format", "stream-json", "--verbose" }),
        "codex" => (Find("codex"), new[] { "exec", "--json", prompt }),
        "gemini" => (Find("gemini"), new[] { "-p", prompt, "--output-format", "stream-json" }),
        _ => throw new InvalidDataException("unsupported_agent")
    };

    static string Find(string n) => CliDiscovery.Find(n) ?? throw new FileNotFoundException($"{n}_not_found");

    static string Required(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new InvalidDataException($"missing_{n}");
    static string FullDirectory(string value) { var p = Path.GetFullPath(value); if (!Directory.Exists(p) || p.StartsWith("\\\\")) throw new InvalidDataException("invalid_project_directory"); return p; }
    static async Task Send(StreamWriter w, object value)
    {
        var line = JsonSerializer.Serialize(value, JsonOptions);
        lock (WriteLock)
        {
            await w.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    static class Calendar
    {
        record Token(string access_token, string refresh_token, long expires_at);
        static string TokenFile => Path.Combine(DataDir, "google-token.bin");
        static Token? Load() { if (!File.Exists(TokenFile)) return null; try { var b = ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser); return JsonSerializer.Deserialize<Token>(b); } catch { return null; } }
        public static async Task<bool> Auth()
        {
            var saved = CredentialStore.LoadGoogle();
            var client = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_ID");
            var secret = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(client)) client = saved?.ClientId;
            if (string.IsNullOrWhiteSpace(secret)) secret = saved?.ClientSecret;
            if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("google_credentials_required_open_setup");
            using var listener = new HttpListener(); listener.Prefixes.Add("http://127.0.0.1:43871/"); listener.Start();
            var uri = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(client)}&redirect_uri=http%3A%2F%2F127.0.0.1%3A43871%2F&response_type=code&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar.readonly&access_type=offline&prompt=consent";
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); var ctx = await listener.GetContextAsync(); var code = ctx.Request.QueryString["code"]; await using (var s = new StreamWriter(ctx.Response.OutputStream)) await s.WriteAsync("Authorization complete. You can close this tab."); ctx.Response.Close(); if (string.IsNullOrEmpty(code)) return false;
            using var http = new HttpClient(); var form = new FormUrlEncodedContent(new Dictionary<string,string> { ["code"] = code, ["client_id"] = client, ["client_secret"] = secret, ["redirect_uri"] = "http://127.0.0.1:43871/", ["grant_type"] = "authorization_code" }); var response = await http.PostAsync("https://oauth2.googleapis.com/token", form); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = doc.RootElement; var token = new Token(root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + root.GetProperty("expires_in").GetInt64()); var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token), null, DataProtectionScope.CurrentUser); File.WriteAllBytes(TokenFile, protectedBytes); return true;
        }
        static async Task<string> AccessToken()
        {
            var t = Load(); if (t is null) throw new InvalidOperationException("calendar_auth_required");
            if (t.expires_at > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return t.access_token;
            var saved = CredentialStore.LoadGoogle();
            var client = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_ID");
            var secret = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(client)) client = saved?.ClientId;
            if (string.IsNullOrWhiteSpace(secret)) secret = saved?.ClientSecret;
            if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("google_oauth_configuration_missing");
            using var http = new HttpClient();
            var form = new FormUrlEncodedContent(new Dictionary<string,string> { ["client_id"] = client, ["client_secret"] = secret, ["refresh_token"] = t.refresh_token, ["grant_type"] = "refresh_token" });
            var response = await http.PostAsync("https://oauth2.googleapis.com/token", form); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = doc.RootElement;
            var refreshed = new Token(root.GetProperty("access_token").GetString()!, t.refresh_token, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + root.GetProperty("expires_in").GetInt64());
            File.WriteAllBytes(TokenFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(refreshed), null, DataProtectionScope.CurrentUser)); return refreshed.access_token;
        }
        static string StartValue(JsonElement item, string key)
        {
            if (!item.TryGetProperty(key, out var value)) return "";
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("dateTime", out var dateTime)) return dateTime.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("date", out var date)) return date.GetString() ?? "";
            return value.ToString();
        }
        public static async Task<object[]> Today()
        {
            var accessToken = await AccessToken(); using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); var now = DateTimeOffset.Now; var url = $"https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&orderBy=startTime&timeMin={Uri.EscapeDataString(now.ToUniversalTime().ToString("o"))}&timeMax={Uri.EscapeDataString(now.Date.AddDays(1).ToUniversalTime().ToString("o"))}"; var doc = JsonDocument.Parse(await http.GetStringAsync(url)); return doc.RootElement.GetProperty("items").EnumerateArray().Select(x => (object)new { id = x.GetProperty("id").GetString(), summary = x.TryGetProperty("summary", out var s) ? s.GetString() : "(untitled)", start = StartValue(x, "start"), end = StartValue(x, "end"), location = x.TryGetProperty("location", out var l) ? l.GetString() : null }).ToArray();
        }
    }
}
