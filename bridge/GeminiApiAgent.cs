using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// ---------------------------------------------------------------------------
// Gemini API coding agent.
//
// Gemini API is a REAL selectable coding-agent provider, not just an API-key
// test. `agent.start` with `"provider":"api"` runs this loop:
//
//   POST /v1beta/models/<model>:streamGenerateContent?alt=sse   (SSE stream)
//   -> model streams text and/or functionCall parts
//   -> every functionCall executes one of the project-root-constrained tools
//        list_files, read_file, search, write_file, edit_file,
//        git_status, git_diff, run_command
//   -> functionResponse is appended to the history and the loop continues
//   -> when the model returns plain text, the agent reports `completed`
//
// Safety guarantees:
//   - All tool paths are resolved inside the project root. UNC paths,
//     absolute external paths, `..` traversal and reparse-point (symlink /
//     mount-point) escapes are rejected.
//   - run_command only allows: git, dotnet, npm, node, python, pytest.
//     Shell metacharacters are rejected outright.
//   - Cancellation goes through a CancellationTokenSource wired to
//     `agent.cancel`; the loop also reacts to pipe disconnects safely.
//   - The API key is only sent as the x-goog-api-key header. It is never
//     written to logs, error messages, or stream events.
// ---------------------------------------------------------------------------
internal static class GeminiApiAgent
{
    const int MaxTurns = 24;
    const int CommandTimeoutMs = 180_000;
    const int MaxResultChars = 20_000;

    static readonly string[] AllowedCommands = { "git", "dotnet", "npm", "node", "python", "pytest" };
    static readonly char[] ShellMetacharacters = { '|', '&', ';', '>', '<', '`', '$', '*', '?', '(', ')', '{', '}', '[', ']', '~', '%', '"', '\'', '\n', '\r' };

    const int FileAttributeReparsePoint = 0x00000040;
    const int InvalidFileAttributes = -1;

    [DllImport("api-ms-win-core-file-l1-1-0.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFileAttributesW", ExactSpelling = true)]
    static extern int GetFileAttributesW(string path);

    public static async Task<string> Run(string id, string project, string prompt, StreamWriter writer, CancellationToken token)
    {
        var key = SecretStore.LoadGemini();
        if (string.IsNullOrWhiteSpace(key))
        {
            await Send(writer, id, "failed", "Gemini API key not saved. Open Setup & Integrations in the bridge and store a key.");
            return "failed";
        }

        using var localToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        var t = localToken.Token;
        bool disconnected = false;

        async Task Send(string phase, string text)
        {
            if (disconnected) return;
            try
            {
                var safe = Sanitize(text, 2_000);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "agent",
                    id,
                    stream = "gemini-api",
                    normalized = new { phase, text = safe },
                    data = safe,
                }, Program.JsonOptions));
            }
            catch
            {
                // Client went away mid-stream: stop the agent cleanly and do
                // not attempt any further writes.
                disconnected = true;
                try { localToken.Cancel(); } catch { }
            }
        }

        var history = new List<Dictionary<string, object?>>
        {
            TextTurn("user", prompt),
        };

        using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", key);

        try
        {
            for (var turn = 0; turn < MaxTurns && !t.IsCancellationRequested && !disconnected; turn++)
            {
                var request = new Dictionary<string, object?>
                {
                    ["systemInstruction"] = new { parts = new[] { new { text = SystemPrompt } } },
                    ["contents"] = history,
                    ["tools"] = new[] { new { function_declarations = ToolDeclarations() } },
                    ["generationConfig"] = new { temperature = 0.2, maxOutputTokens = 8192 },
                };
                using var requestMessage = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiApi.Model}:streamGenerateContent?alt=sse")
                {
                    Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"),
                };

                using var response = await http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, t);
                if (!response.IsSuccessStatusCode)
                {
                    await Send("failed", "Gemini API request failed with status " + (int)response.StatusCode + ".");
                    return "failed";
                }

                var modelText = new StringBuilder();
                var pendingCalls = new List<Dictionary<string, object?>>();
                var streamError = false;

                await using var stream = await response.Content.ReadAsStreamAsync(t);
                using var reader = new StreamReader(stream);
                while (true)
                {
                    t.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(t);
                    if (line is null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line.Substring("data:".Length).Trim();
                    if (data.Length == 0 || data == "[DONE]") continue;

                    JsonElement chunk;
                    try { using var doc = JsonDocument.Parse(data); chunk = doc.RootElement.Clone(); }
                    catch (JsonException) { continue; }

                    if (chunk.TryGetProperty("error", out _))
                    {
                        streamError = true;
                        break;
                    }
                    if (!chunk.TryGetProperty("candidates", out var candidates) ||
                        candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                    {
                        continue;
                    }
                    var candidate = candidates[0];
                    if (candidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            {
                                var piece = text.GetString() ?? "";
                                if (piece.Length == 0) continue;
                                modelText.Append(piece);
                                await Send("working", piece);
                            }
                            else if (part.TryGetProperty("functionCall", out var fc))
                            {
                                var callName = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                var callArgs = fc.TryGetProperty("args", out var a) ? a : default;
                                pendingCalls.Add(new Dictionary<string, object?>
                                {
                                    ["name"] = callName,
                                    ["args"] = callArgs.ValueKind == JsonValueKind.Undefined ? new Dictionary<string, string>() : CallArgsToObject(callArgs),
                                });
                                await Send(PhaseForTool(callName), callName + " " + ToolArgSummary(callArgs));
                            }
                        }
                    }
                }

                if (streamError)
                {
                    await Send("failed", "Gemini API stream returned an error. Re-run the agent or use the CLI provider.");
                    return "failed";
                }
                if (disconnected) return "failed";
                if (t.IsCancellationRequested) { await Send("cancelled", "Cancelled."); return "cancelled"; }

                history.Add(FunctionCallTurn(modelText.ToString(), pendingCalls));
                if (pendingCalls.Count == 0)
                {
                    var summary = modelText.Length > 0 ? CliDiscovery.FirstLine(modelText.ToString()) : "Done.";
                    await Send("completed", summary);
                    return "completed";
                }

                foreach (var call in pendingCalls)
                {
                    var name = call["name"] as string ?? "";
                    var args = call["args"] as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                    var (phase, summary, resultText) = await ExecuteTool(project, name, args, Send, t);
                    history.Add(FunctionResponseTurn(name, resultText));
                    if (disconnected) return "failed";
                    if (t.IsCancellationRequested) { await Send("cancelled", "Cancelled."); return "cancelled"; }
                }
            }
            await Send("completed", "Gemini API agent finished (turn limit reached).");
            return "completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await Send("cancelled", "Cancelled.");
            return "cancelled";
        }
        catch
        {
            // Safe disconnect handling: the stream may end mid-transfer
            // (network drop, API reset, client gone). Report a generic
            // failure that never contains the key, the URL, or the payload.
            if (!token.IsCancellationRequested && !disconnected)
            {
                await Send("failed", "Gemini API stream ended unexpectedly.");
            }
            return token.IsCancellationRequested ? "cancelled" : "failed";
        }
    }

    const string SystemPrompt = "You are a coding agent working inside the user's project directory on Windows. " +
        "Use the tools to list files, read code, search, write or edit files, inspect git state and run allowlisted build/test commands. " +
        "Tool arguments must be safe: paths stay inside the project root, commands come from the allowlist (git, dotnet, npm, node, python, pytest). " +
        "When finished, reply with a concise plain-text summary of the changes.";

    // --- SSE payload helpers -------------------------------------------------

    static Dictionary<string, object?> TextTurn(string role, string text) => new()
    {
        ["role"] = role,
        ["parts"] = new[] { new Dictionary<string, object?> { ["text"] = text } },
    };

    static Dictionary<string, object?> FunctionCallTurn(string text, List<Dictionary<string, object?>> calls)
    {
        var parts = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(new Dictionary<string, object?> { ["text"] = text });
        }
        foreach (var call in calls)
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["functionCall"] = new Dictionary<string, object?>
                {
                    ["name"] = call["name"],
                    ["args"] = call["args"],
                },
            });
        }
        return new Dictionary<string, object?>
        {
            ["role"] = "model",
            ["parts"] = parts,
        };
    }

    static Dictionary<string, object?> FunctionResponseTurn(string name, string result) => new()
    {
        ["role"] = "user",
        ["parts"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["functionResponse"] = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["response"] = new Dictionary<string, object?> { ["result"] = result },
                },
            },
        },
    };

    static Dictionary<string, object?> CallArgsToObject(JsonElement args)
    {
        var map = new Dictionary<string, object?>();
        if (args.ValueKind != JsonValueKind.Object) return map;
        foreach (var property in args.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }
        return map;
    }

    static string ToolArgSummary(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        var parts = new List<string>();
        foreach (var property in args.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString() ?? "";
                parts.Add(property.Name + "=" + (value.Length > 60 ? value.Substring(0, 60) + "\u2026" : value));
            }
        }
        var summary = string.Join(" ", parts);
        return summary.Length > 160 ? summary.Substring(0, 160) + "\u2026" : summary;
    }

    static string Sanitize(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = text.Replace('\r', ' ').Replace('\t', ' ');
        return clean.Length <= maxChars ? clean : clean.Substring(clean.Length - maxChars);
    }

    // --- Tool declarations ---------------------------------------------------

    static object[] ToolDeclarations()
    {
        static object Decl(string name, string description, params (string Key, string Type, string Desc)[] parameters)
        {
            var d = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
            };
            if (parameters.Length > 0)
            {
                var props = new Dictionary<string, object?>();
                foreach (var (key, type, desc) in parameters)
                    props[key] = new Dictionary<string, object?> { ["type"] = type, ["description"] = desc };
                d["parameters"] = new Dictionary<string, object?> { ["type"] = "OBJECT", ["properties"] = props };
            }
            return d;
        }
        return new object[]
        {
            Decl("list_files", "List files and directories (relative path, default '.'), max 200 entries.",
                ("path", "STRING", "Directory relative to the project root.")),
            Decl("read_file", "Read a text file from the project.",
                ("path", "STRING", "File path relative to the project root.")),
            Decl("search", "Search file names and file contents for a query string.",
                ("query", "STRING", "Text to search for."),
                ("path", "STRING", "Optional directory to search in, relative to the project root.")),
            Decl("write_file", "Create or overwrite a file inside the project.",
                ("path", "STRING", "File path relative to the project root."),
                ("content", "STRING", "Full file content.")),
            Decl("edit_file", "Replace the first occurrence of old_text with new_text inside a project file.",
                ("path", "STRING", "File path relative to the project root."),
                ("old_text", "STRING", "Exact text to replace."),
                ("new_text", "STRING", "Replacement text.")),
            Decl("git_status", "Show the project git status (short format)."),
            Decl("git_diff", "Show the project git diff. Pass staged=true for the staged diff.",
                ("staged", "STRING", "'true' to show staged changes, otherwise working tree.")),
            Decl("run_command", "Run one allowlisted command (git, dotnet, npm, node, python, pytest) in the project root. Shell metacharacters are rejected.",
                ("command", "STRING", "Full command line, e.g. 'git status --short'.")),
        };
    }

    static string PhaseForTool(string tool) => tool switch
    {
        "read_file" => "reading_file",
        "write_file" or "edit_file" => "editing_file",
        "run_command" => "running_command",
        _ => "running_tool",
    };

    // --- Tool execution -------------------------------------------------------

    static async Task<(string Phase, string Summary, string Result)> ExecuteTool(
        string project, string name, Dictionary<string, object?> args,
        Func<string, string, Task> send, CancellationToken token)
    {
        string Arg(string key) => args.TryGetValue(key, out var value) ? value as string ?? "" : "";
        var (phase, summary, result) = name switch
        {
            "list_files" => ToolListFiles(project, Arg("path")),
            "read_file" => ToolReadFile(project, Arg("path")),
            "search" => ToolSearch(project, Arg("query"), Arg("path")),
            "write_file" => ToolWriteFile(project, Arg("path"), Arg("content")),
            "edit_file" => ToolEditFile(project, Arg("path"), Arg("old_text"), Arg("new_text")),
            "git_status" => await ToolGit(project, new[] { "status", "--short" }, token),
            "git_diff" => await ToolGit(project, Arg("staged").Equals("true", StringComparison.OrdinalIgnoreCase) ? new[] { "diff", "--staged" } : new[] { "diff" }, token),
            "run_command" => await ToolRunCommand(project, Arg("command"), token),
            _ => ("running_tool", "unknown tool " + name, "Error: unknown tool '" + name + "'."),
        };
        await send("tool_result", summary);
        return (phase, summary, result);
    }

    static (string Phase, string Summary, string Result) ToolListFiles(string project, string relative)
    {
        try
        {
            var dir = ResolveSafePath(project, relative);
            if (!Directory.Exists(dir))
                return ("running_tool", "list_files " + relative, "Error: directory not found: " + relative);
            var entries = new List<string>();
            foreach (var entry in Directory.EnumerateDirectories(dir))
            {
                entries.Add(Path.GetFileName(entry) + "/");
                if (entries.Count >= 200) break;
            }
            foreach (var entry in Directory.EnumerateFiles(dir))
            {
                entries.Add(Path.GetFileName(entry));
                if (entries.Count >= 200) break;
            }
            return ("running_tool", "listed " + entries.Count + " entries in " + relative,
                entries.Count > 0 ? string.Join("\n", entries) : "(empty directory)");
        }
        catch (Exception e) { return ("running_tool", "list_files failed", "Error: " + SafeMessage(e)); }
    }

    static (string Phase, string Summary, string Result) ToolReadFile(string project, string relative)
    {
        try
        {
            var path = ResolveSafePath(project, relative);
            if (!File.Exists(path))
                return ("reading_file", "read_file " + relative, "Error: file not found: " + relative);
            var info = new FileInfo(path);
            if (info.Length > 256 * 1024)
                return ("reading_file", "read_file " + relative + " (too large)",
                    "Error: file is larger than 256 KB (" + info.Length + " bytes). Use search instead.");
            var text = File.ReadAllText(path);
            return ("reading_file", "read " + info.Length + " bytes from " + relative, Truncate(text));
        }
        catch (Exception e) { return ("reading_file", "read_file failed", "Error: " + SafeMessage(e)); }
    }

    static (string Phase, string Summary, string Result) ToolSearch(string project, string query, string relative)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return ("running_tool", "search", "Error: query is required.");
            var baseDir = ResolveSafePath(project, relative);
            if (!Directory.Exists(baseDir))
                return ("running_tool", "search " + query, "Error: directory not found: " + relative);
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".git", "node_modules", "bin", "obj", "build", "dist", ".venv", ".next", "coverage", "target" };
            var matches = new List<string>();
            var filesScanned = 0;
            var nameMatches = new List<string>();
            var dirs = new Queue<string>();
            dirs.Enqueue(baseDir);
            while (dirs.Count > 0 && matches.Count + nameMatches.Count < 50 && filesScanned < 3000)
            {
                var dir = dirs.Dequeue();
                var relativeDir = RelativeToProject(project, dir);
                if (HasSkippedComponent(relativeDir, skip)) continue;
                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); } catch { continue; }
                foreach (var sub in subDirs) dirs.Enqueue(sub);
                string[] files;
                try { files = Directory.GetFiles(dir); } catch { continue; }
                foreach (var file in files)
                {
                    if (matches.Count + nameMatches.Count >= 50 || filesScanned >= 3000) break;
                    var fileName = Path.GetFileName(file);
                    var relativeFile = RelativeToProject(project, file);
                    if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        nameMatches.Add("name: " + relativeFile);
                        continue;
                    }
                    if (HasSkippedComponent(Path.GetFileName(Path.GetDirectoryName(file) ?? ""), skip)) continue;
                    filesScanned++;
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > 1024 * 1024) continue;
                        var text = File.ReadAllText(file);
                        if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        var lineStart = text.LastIndexOf('\n', index) + 1;
                        var lineEnd = text.IndexOf('\n', index);
                        if (lineEnd < 0) lineEnd = text.Length;
                        var lineText = text.Substring(lineStart, lineEnd - lineStart).Trim();
                        if (lineText.Length > 160) lineText = lineText.Substring(0, 160) + "\u2026";
                        matches.Add(relativeFile + ": " + lineText);
                    }
                    catch { }
                }
            }
            var all = nameMatches.Concat(matches).ToList();
            return ("running_tool", (all.Count + " match(es) for " + query),
                all.Count > 0 ? string.Join("\n", all) : "(no matches)");
        }
        catch (Exception e) { return ("running_tool", "search failed", "Error: " + SafeMessage(e)); }
    }

    static bool HasSkippedComponent(string relativePath, HashSet<string> skip)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (skip.Contains(part)) return true;
        return false;
    }

    static (string Phase, string Summary, string Result) ToolWriteFile(string project, string relative, string content)
    {
        try
        {
            var path = ResolveSafePath(project, relative);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            return ("editing_file", "wrote " + content.Length + " chars to " + relative,
                "Wrote " + new FileInfo(path).Length + " bytes to " + relative);
        }
        catch (Exception e) { return ("editing_file", "write_file failed", "Error: " + SafeMessage(e)); }
    }

    static (string Phase, string Summary, string Result) ToolEditFile(string project, string relative, string oldText, string newText)
    {
        try
        {
            var path = ResolveSafePath(project, relative);
            if (!File.Exists(path))
                return ("editing_file", "edit_file " + relative, "Error: file not found: " + relative);
            if (string.IsNullOrEmpty(oldText))
                return ("editing_file", "edit_file " + relative, "Error: old_text is required.");
            var text = File.ReadAllText(path);
            var index = text.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
                return ("editing_file", "edit_file " + relative, "Error: old_text not found in " + relative + ". Re-read the file first.");
            bool moreOccurrences = text[(index + oldText.Length)..].Contains(oldText, StringComparison.Ordinal);
            text = text[..index] + newText + text[(index + oldText.Length)..];
            File.WriteAllText(path, text);
            return ("editing_file", "edited " + relative,
                "Replaced first occurrence in " + relative + (moreOccurrences ? " (more occurrences remain)" : ""));
        }
        catch (Exception e) { return ("editing_file", "edit_file failed", "Error: " + SafeMessage(e)); }
    }

    static async Task<(string Phase, string Summary, string Result)> ToolGit(string project, string[] args, CancellationToken token)
    {
        var git = CliDiscovery.Find("git");
        if (git is null)
            return ("running_tool", "git " + string.Join(" ", args), "Error: git executable not found.");
        try
        {
            var psi = new ProcessStartInfo(git)
            {
                WorkingDirectory = project,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return ("running_tool", "git failed", "Error: git could not be started.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(CommandTimeoutMs))
            {
                try { p.Kill(true); } catch { }
                return ("running_tool", "git timed out", "Error: git timed out.");
            }
            var stdout = Truncate(AwaitSafe(outTask));
            var stderr = Truncate(AwaitSafe(errTask));
            var summary = "git " + args[0] + (p.ExitCode == 0 ? " ok" : " failed (" + p.ExitCode + ")");
            return ("running_tool", summary, (stdout.Length > 0 ? stdout + "\n" : "") + (stderr.Length > 0 ? "stderr: " + stderr : ""));
        }
        catch (Exception e) { return ("running_tool", "git failed", "Error: " + SafeMessage(e)); }
    }

    static async Task<(string Phase, string Summary, string Result)> ToolRunCommand(string project, string command, CancellationToken token)
    {
        try { ValidateCommand(command); }
        catch (InvalidDataException e)
        {
            return ("running_command", "command rejected", "Error: " + e.Message);
        }
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var psi = new ProcessStartInfo(tokens[0])
            {
                WorkingDirectory = project,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in tokens.Skip(1)) psi.ArgumentList.Add(a);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            linked.CancelAfter(CommandTimeoutMs);
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!p.Start())
                return ("running_command", "command start failed", "Error: command could not be started.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try { p.Kill(true); } catch { }
                return ("running_command", "command timed out", "Error: command timed out after " + (CommandTimeoutMs / 1000) + " seconds.");
            }
            var stdout = Truncate(AwaitSafe(outTask));
            var stderr = Truncate(AwaitSafe(errTask));
            var summary = "command " + tokens[0] + (p.ExitCode == 0 ? " completed" : " failed (" + p.ExitCode + ")");
            var result = (stdout.Length > 0 ? stdout + "\n" : "") + (stderr.Length > 0 ? "stderr: " + stderr : "");
            if (result.Length == 0) result = "(no output)";
            return ("running_command", summary, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return ("running_command", "command failed", "Error: " + SafeMessage(e)); }
    }

    // --- Safety helpers --------------------------------------------------------

    static string SafeMessage(Exception e)
    {
        var message = e.Message ?? "unknown error";
        return message.Length > 300 ? message.Substring(0, 300) : message;
    }

    static string Truncate(string value) => value.Length <= MaxResultChars ? value : value.Substring(0, MaxResultChars) + "\n\u2026 (truncated)";

    static string AwaitSafe(Task<string> task)
    {
        try { return task.GetAwaiter().GetResult(); } catch { return ""; }
    }

    static string RelativeToProject(string project, string path)
    {
        var root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : Path.GetFileName(full);
    }

    // Resolves a model-supplied path inside the project root and rejects
    // UNC paths, absolute paths outside the root, `..` traversal and any
    // component that is a reparse point (symlink / mount / junction) which
    // could be used to escape the sandbox.
    static string ResolveSafePath(string project, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException("path is required");
        var trimmed = relative.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (trimmed.StartsWith("\\\\"))
            throw new InvalidDataException("UNC paths are not allowed");
        var root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(root, trimmed));
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("path escapes the project root");
        }
        RejectReparseEscapes(root, full, trimmed);
        return full;
    }

    // Walks both the raw (pre-normalization) components and the normalized
    // target path. Textual normalization can hide an escape that goes through
    // a reparse point (`link\..\..\x`), so every raw component that exists on
    // disk is checked for the reparse-point attribute as well.
    static void RejectReparseEscapes(string root, string full, string relative)
    {
        var walked = root;
        foreach (var part in relative.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                var parent = Path.GetDirectoryName(walked.TrimEnd(Path.DirectorySeparatorChar));
                if (parent is null) break;
                walked = parent;
                continue;
            }
            walked = Path.Combine(walked, part);
            RejectReparseComponent(walked);
        }
        var current = string.Empty;
        foreach (var part in full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = string.IsNullOrEmpty(current) ? part : current + Path.DirectorySeparatorChar + part;
            RejectReparseComponent(current);
        }
        RejectReparseComponent(root);
    }

    static void RejectReparseComponent(string path)
    {
        if (path.Length <= 3) return; // drive root like C:\
        var attributes = GetFileAttributesW(path);
        if (attributes != InvalidFileAttributes && (attributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidDataException("reparse point escape is not allowed");
        }
    }

    // run_command allowlist. Rejects shell metacharacters (pipes, redirects,
    // command chaining, command substitution, wildcards) and any first token
    // whose base name is not on the allowlist.
    static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidDataException("command is required");
        foreach (var c in ShellMetacharacters)
        {
            if (command.Contains(c))
                throw new InvalidDataException("shell metacharacters are not allowed");
        }
        var first = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (first.Length == 0)
            throw new InvalidDataException("command is required");
        var baseName = Path.GetFileNameWithoutExtension(first[0]).ToLowerInvariant();
        if (!AllowedCommands.Contains(baseName))
            throw new InvalidDataException("command '" + baseName + "' is not on the allowlist (git, dotnet, npm, node, python, pytest)");
    }
}
