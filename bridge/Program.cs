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
// Protocol: one UTF-8 JSON object per line over \\.\pipe\DynamicIslandBridge-v2.
// No command received here is executed by a shell. Agent arguments are passed as
// ProcessStartInfo.ArgumentList, and project roots must be existing directories.
internal static class Program
{
    const string PipeName = "DynamicIslandBridge-v2";
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIslandBridge");
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, Process> Jobs = new(StringComparer.Ordinal);
    static readonly object JobsLock = new();

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);
        while (true)
        {
            try { await ServeClient(await CreatePipe()); }
            catch (Exception e) { Console.Error.WriteLine($"bridge: {e.Message}"); }
        }
    }

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

    static async Task Dispatch(JsonElement r, StreamWriter w)
    {
        var op = r.TryGetProperty("op", out var o) ? o.GetString() : null;
        switch (op)
        {
            case "status": await Send(w, new { ok = true, bridge = "online", agents = DetectAgents() }); break;
            case "calendar.auth": await Send(w, new { ok = await Calendar.Auth(), state = "authorized" }); break;
            case "calendar.today": await Send(w, new { ok = true, events = await Calendar.Today() }); break;
            case "agent.start": await StartAgent(r, w); break;
            case "agent.cancel": await Cancel(r, w); break;
            case "agent.resume": await ResumeAgent(r, w); break;
            default: await Send(w, new { ok = false, error = "unknown_operation" }); break;
        }
    }

    static async Task StartAgent(JsonElement r, StreamWriter w)
    {
        var agent = Required(r, "agent").ToLowerInvariant();
        var root = FullDirectory(Required(r, "project"));
        var prompt = Required(r, "prompt");
        if (prompt.Length > 32_000) throw new InvalidDataException("prompt_too_large");
        var (file, args) = AgentCommand(agent, prompt);
        var psi = new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!p.Start()) throw new InvalidOperationException("agent_start_failed");
        var id = Guid.NewGuid().ToString("N"); lock (JobsLock) Jobs[id] = p;
        await Send(w, new { ok = true, id, agent, project = root });
        _ = StreamOutput(id, p, w);
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

    static async Task StreamOutput(string id, Process p, StreamWriter w)
    {
        async Task Pump(StreamReader s, string stream) { string? line; while ((line = await s.ReadLineAsync()) != null) try { await Send(w, new { type = "agent", id, stream, normalized = NormalizeEvent(line), data = line }); } catch { break; } }
        await Task.WhenAll(Pump(p.StandardOutput, "stdout"), Pump(p.StandardError, "stderr"));
        await p.WaitForExitAsync(); try { await Send(w, new { type = "exit", id, code = p.ExitCode }); } catch { }
        lock (JobsLock) Jobs.Remove(id); p.Dispose();
    }

    static async Task Cancel(JsonElement r, StreamWriter w)
    { var id = Required(r, "id"); lock (JobsLock) if (Jobs.TryGetValue(id, out var p)) { try { if (!p.HasExited) p.Kill(true); } catch { } } await Send(w, new { ok = true, id, state = "cancelling" }); }
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
        var psi = new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }; foreach (var arg in args) psi.ArgumentList.Add(arg);
        var process = new Process { StartInfo = psi }; if (!process.Start()) throw new InvalidOperationException("agent_resume_failed");
        var id = Guid.NewGuid().ToString("N"); lock (JobsLock) Jobs[id] = process; await Send(w, new { ok = true, id, agent, resumed = session }); _ = StreamOutput(id, process, w);
    }

    static (string, string[]) AgentCommand(string agent, string prompt) => agent switch
    {
        "claude" => (Find("claude"), new[] { "-p", prompt, "--output-format", "stream-json", "--verbose" }),
        "codex" => (Find("codex"), new[] { "exec", "--json", prompt }),
        "gemini" => (Find("gemini"), new[] { "-p", prompt, "--output-format", "stream-json" }),
        _ => throw new InvalidDataException("unsupported_agent")
    };

    static Dictionary<string, bool> DetectAgents() => new() { ["claude"] = Exists("claude"), ["codex"] = Exists("codex"), ["gemini"] = Exists("gemini") };
    static bool Exists(string n) { try { Find(n); return true; } catch { return false; } }
    static string Find(string n)
    { foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) foreach (var ext in new[] { ".exe", ".cmd", ".bat", "" }) { var p = Path.Combine(dir, n + ext); if (File.Exists(p)) return p; } throw new FileNotFoundException($"{n}_not_found"); }
    static string Required(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new InvalidDataException($"missing_{n}");
    static string FullDirectory(string value) { var p = Path.GetFullPath(value); if (!Directory.Exists(p) || p.StartsWith("\\\\")) throw new InvalidDataException("invalid_project_directory"); return p; }
    static Task Send(StreamWriter w, object value) => w.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));

    static class Calendar
    {
        record Token(string access_token, string refresh_token, long expires_at);
        static string TokenFile => Path.Combine(DataDir, "google-token.bin");
        static Token? Load() { if (!File.Exists(TokenFile)) return null; try { var b = ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser); return JsonSerializer.Deserialize<Token>(b); } catch { return null; } }
        public static async Task<bool> Auth()
        {
            var client = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_ID"); var secret = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("set_google_oauth_environment_variables");
            using var listener = new HttpListener(); listener.Prefixes.Add("http://127.0.0.1:43871/"); listener.Start();
            var uri = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(client)}&redirect_uri=http%3A%2F%2F127.0.0.1%3A43871%2F&response_type=code&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar.readonly&access_type=offline&prompt=consent";
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); var ctx = await listener.GetContextAsync(); var code = ctx.Request.QueryString["code"]; await using (var s = new StreamWriter(ctx.Response.OutputStream)) await s.WriteAsync("Authorization complete. You can close this tab."); ctx.Response.Close(); if (string.IsNullOrEmpty(code)) return false;
            using var http = new HttpClient(); var form = new FormUrlEncodedContent(new Dictionary<string,string> { ["code"] = code, ["client_id"] = client, ["client_secret"] = secret, ["redirect_uri"] = "http://127.0.0.1:43871/", ["grant_type"] = "authorization_code" }); var response = await http.PostAsync("https://oauth2.googleapis.com/token", form); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = doc.RootElement; var token = new Token(root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + root.GetProperty("expires_in").GetInt64()); var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token), null, DataProtectionScope.CurrentUser); File.WriteAllBytes(TokenFile, protectedBytes); return true;
        }
        static async Task<string> AccessToken()
        {
            var t = Load(); if (t is null) throw new InvalidOperationException("calendar_auth_required");
            if (t.expires_at > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return t.access_token;
            var client = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_ID");
            var secret = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET");
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
