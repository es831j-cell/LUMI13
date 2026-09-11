using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private bool _lumiDesktopRuntimeReady;
    private string _runtimeSessionId = Guid.NewGuid().ToString("N");
    private string RuntimeRoot => Path.Combine(WorkstationRoot, "Runtime");
    private string RuntimeLedgerPath => Path.Combine(RuntimeRoot, "runtime-ledger.jsonl");

    private static readonly string[] LumiDesktopTools =
    {
        "runtime.status",
        "lumi.sync_phone",
        "lumi.phone_status",
        "lumi.diagnostics",
        "lumi.build",
        "lumi.deploy",
        "android_studio.open",
        "workspace.list",
        "workspace.read",
        "workspace.write",
        "workspace.append",
        "git.status",
        "git.diff",
        "git.log"
    };

    private void InitializeLumiDesktopRuntime()
    {
        if (_lumiDesktopRuntimeReady) return;
        _lumiDesktopRuntimeReady = true;

        Directory.CreateDirectory(RuntimeRoot);
        var state = new
        {
            format = "LumiDesktopRuntimeState",
            formatVersion = 1,
            name = "Lumi",
            activeHost = "windows-workstation",
            sessionId = _runtimeSessionId,
            startedAt = DateTimeOffset.Now,
            toolRegistry = LumiDesktopTools,
            note = "AI providers are reasoning engines. Lumi Desktop Runtime owns tool execution and evidence."
        };
        File.WriteAllText(Path.Combine(RuntimeRoot, "runtime-state.json"),
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

        AddRuntimeStatusToDashboard();
        WriteRuntimeLedger("runtime.start", new { host = "windows-workstation", tools = LumiDesktopTools.Length });
        AppendCommand($"LUMI > Desktop Runtime ACTIVE on this workstation. {LumiDesktopTools.Length} local tools registered.");
        AppendDiagnostic($"Lumi Desktop Runtime active. session={_runtimeSessionId}; tools={string.Join(",", LumiDesktopTools)}");
    }

    private void AddRuntimeStatusToDashboard()
    {
        var header = VisualDescendants<TextBlock>(this)
            .FirstOrDefault(x => x.Text.Equals("PROJECT STATUS", StringComparison.OrdinalIgnoreCase));
        if (header?.Parent is not StackPanel panel) return;
        if (panel.Children.OfType<TextBlock>().Any(x => x.Tag?.ToString() == "lumi-runtime-status")) return;

        panel.Children.Insert(Math.Min(1, panel.Children.Count), new TextBlock
        {
            Tag = "lumi-runtime-status",
            Text = "●   Lumi Runtime            ACTIVE · WINDOWS HOST",
            Foreground = new SolidColorBrush(Color.FromRgb(73, 229, 142)),
            Margin = new Thickness(0, 6, 0, 6),
            FontWeight = FontWeights.SemiBold
        });
    }

    private async Task ExecuteLumiDesktopPromptAsync(ProviderConnection provider, string instruction)
    {
        if (!_lumiDesktopRuntimeReady) InitializeLumiDesktopRuntime();

        CommandInputBox.Clear();
        AppendCommand($"YOU > {instruction}");
        SaveInstruction(instruction, "LUMI_RUNTIME_RECEIVED");
        SendCommandButton.IsEnabled = false;
        StatusText.Text = $"Lumi reasoning via {provider.Name}...";
        WriteRuntimeLedger("instruction", new { instruction, provider = provider.Name, providerType = provider.Type });

        var evidence = new StringBuilder();
        try
        {
            for (var step = 1; step <= 6; step++)
            {
                var plannerPrompt = BuildRuntimePlannerPrompt(instruction, evidence.ToString(), step);
                var raw = await CallProviderAsync(provider, plannerPrompt);
                var decision = ParseRuntimeDecision(raw);

                WriteRuntimeLedger("reasoning.decision", new
                {
                    step,
                    provider = provider.Name,
                    decision.Reply,
                    decision.Tool,
                    decision.Done
                });

                if (decision.Done || string.IsNullOrWhiteSpace(decision.Tool))
                {
                    var reply = string.IsNullOrWhiteSpace(decision.Reply)
                        ? "I completed the reasoning pass, but the provider returned no final response."
                        : decision.Reply.Trim();
                    AppendCommand($"LUMI > {reply}");
                    StatusText.Text = $"Lumi ready · reasoning: {provider.Name}";
                    SaveInstruction(instruction, "LUMI_RUNTIME_ANSWERED");
                    return;
                }

                StatusText.Text = $"Lumi using tool: {decision.Tool}";
                var result = await ExecuteLumiToolAsync(decision.Tool, decision.Args, instruction);
                evidence.AppendLine($"STEP {step} TOOL {decision.Tool}");
                evidence.AppendLine(result);
                WriteRuntimeLedger("tool.result", new { step, tool = decision.Tool, result = Limit(result, 6000) });
                AppendDiagnostic($"Runtime tool {decision.Tool}: {Limit(result, 1500)}");
            }

            AppendCommand("LUMI > I reached the six-step workstation tool limit for this instruction. The completed tool evidence is in the Runtime ledger.");
            StatusText.Text = "Lumi runtime step limit reached";
        }
        catch (Exception ex)
        {
            AppendCommand($"LUMI > Desktop runtime error: {ex.Message}");
            AppendDiagnostic("Desktop runtime failure: " + ex);
            WriteRuntimeLedger("runtime.error", new { error = ex.ToString() });
            StatusText.Text = "Lumi runtime error";
        }
        finally
        {
            SendCommandButton.IsEnabled = true;
            CommandInputBox.Focus();
        }
    }

    private string BuildRuntimePlannerPrompt(string userInstruction, string evidence, int step)
    {
        var phone = string.IsNullOrWhiteSpace(_deviceSerial) ? "not currently bound" : _deviceSerial;
        var source = File.Exists(Path.Combine(WorkstationRoot, "Live", "source", "gradlew.bat")) ? "commissioned" : "not commissioned";

        return $"""
You are a reasoning engine assisting Lumi Desktop Runtime. You are NOT Lumi and you do NOT execute tools.
Lumi is the persistent assistant/orchestrator running on a Windows workstation. Lumi owns local actions and evidence.
Return exactly one JSON object and no markdown.

Schema:
{{"reply":"short text for Lumi to say","tool":"tool.name or empty","args":{{"path":"optional","content":"optional"}},"done":true_or_false}}

Rules:
- If a real local action is needed, request exactly one tool and set done=false.
- Never claim an action happened until tool evidence below proves it.
- Prefer inspection before modification.
- Do not request tools outside the registry.
- lumi.deploy is permitted only when the user's original wording clearly asks to install/deploy/update the phone.
- workspace paths must stay inside Lumi Workstation.
- Finish with tool="" and done=true when enough evidence exists.

Tool registry:
{string.Join("\n", LumiDesktopTools.Select(x => "- " + x))}

Runtime context:
- host: windows-workstation
- runtime session: {_runtimeSessionId}
- phone serial: {phone}
- live Android source: {source}
- step: {step}/6

Original user instruction:
{userInstruction}

Evidence from tools already executed:
{(string.IsNullOrWhiteSpace(evidence) ? "(none)" : Limit(evidence, 12000))}
""";
    }

    private async Task<string> ExecuteLumiToolAsync(string tool, JsonElement args, string originalInstruction)
    {
        tool = tool.Trim().ToLowerInvariant();
        if (!LumiDesktopTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            return $"TOOL FAIL: '{tool}' is not in Lumi's workstation registry.";

        switch (tool)
        {
            case "runtime.status":
                return RuntimeStatus();

            case "lumi.sync_phone":
                await SyncLumiToWorkstationAsync(userRequested: true);
                return $"SYNC RESULT: {PhoneStateText.Text}; {StatusText.Text}; folder={_lastDockFolder ?? "none"}";

            case "lumi.phone_status":
                await RefreshPhoneAsync();
                return $"PHONE RESULT: {PhoneStateText.Text}; {PhoneDetailText.Text.Replace(Environment.NewLine, " | ")}";

            case "lumi.diagnostics":
                await CaptureDiagnosticsAsync();
                return $"DIAGNOSTICS RESULT: {StatusText.Text}";

            case "lumi.build":
                await BuildLiveSourceAsync(false);
                return $"BUILD RESULT: {StatusText.Text}";

            case "lumi.deploy":
                if (!ExplicitDeployIntent(originalInstruction))
                    return "DEPLOY BLOCKED: original user instruction did not explicitly request install/deploy/update.";
                await BuildLiveSourceAsync(true);
                return $"DEPLOY RESULT: {StatusText.Text}";

            case "android_studio.open":
                OpenAndroidStudio();
                return $"ANDROID STUDIO RESULT: {StatusText.Text}";

            case "workspace.list":
                return WorkspaceList(Arg(args, "path"));

            case "workspace.read":
                return WorkspaceRead(Arg(args, "path"));

            case "workspace.write":
                return WorkspaceWrite(Arg(args, "path"), Arg(args, "content"), append: false);

            case "workspace.append":
                return WorkspaceWrite(Arg(args, "path"), Arg(args, "content"), append: true);

            case "git.status":
                return await RunGitAsync("status --short --branch");

            case "git.diff":
                return await RunGitAsync("diff --stat && git diff -- . ':!*.lock'");

            case "git.log":
                return await RunGitAsync("log -8 --oneline --decorate");

            default:
                return "TOOL FAIL: unhandled tool.";
        }
    }

    private string RuntimeStatus()
    {
        var live = Path.Combine(WorkstationRoot, "Live", "source");
        return $"RUNTIME ACTIVE; host=windows-workstation; session={_runtimeSessionId}; tools={LumiDesktopTools.Length}; phone={_deviceSerial ?? "none"}; dockFolder={_lastDockFolder ?? "none"}; liveSource={(File.Exists(Path.Combine(live, "gradlew.bat")) ? "ready" : "missing")}; providers={_providerConnections.Count}.";
    }

    private string WorkspaceList(string? relative)
    {
        var dir = SafeWorkspacePath(relative, mustExist: false);
        if (!Directory.Exists(dir)) return $"LIST FAIL: directory does not exist: {dir}";
        var entries = Directory.EnumerateFileSystemEntries(dir)
            .Take(200)
            .Select(p => Directory.Exists(p) ? "DIR  " + Path.GetFileName(p) : "FILE " + Path.GetFileName(p));
        return "LIST PASS: " + Path.GetRelativePath(WorkstationRoot, dir) + Environment.NewLine + string.Join(Environment.NewLine, entries);
    }

    private string WorkspaceRead(string? relative)
    {
        var path = SafeWorkspacePath(relative, mustExist: true);
        if (Directory.Exists(path)) return "READ FAIL: path is a directory.";
        var info = new FileInfo(path);
        if (info.Length > 2_000_000) return $"READ FAIL: file is {info.Length} bytes; runtime read limit is 2 MB.";
        var text = File.ReadAllText(path);
        return $"READ PASS: {Path.GetRelativePath(WorkstationRoot, path)} ({info.Length} bytes){Environment.NewLine}{Limit(text, 50000)}";
    }

    private string WorkspaceWrite(string? relative, string? content, bool append)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "WRITE FAIL: path is required.";
        content ??= string.Empty;
        if (content.Length > 250_000) return "WRITE FAIL: content exceeds 250,000 character runtime limit.";
        var path = SafeWorkspacePath(relative, mustExist: false);
        if (Directory.Exists(path)) return "WRITE FAIL: path is a directory.";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (append) File.AppendAllText(path, content);
        else File.WriteAllText(path, content);
        return $"WRITE PASS: {(append ? "appended" : "wrote")} {content.Length} characters to {Path.GetRelativePath(WorkstationRoot, path)}";
    }

    private async Task<string> RunGitAsync(string arguments)
    {
        var repo = Path.Combine(WorkstationRoot, "Live", "source");
        if (!Directory.Exists(Path.Combine(repo, ".git"))) return "GIT FAIL: live source repository is not commissioned on workstation.";
        var result = await RunProcessAsync("cmd.exe", $"/d /s /c \"cd /d \\\"{repo}\\\" && git {arguments}\"", repo, 120000);
        return $"GIT EXIT {result.ExitCode}{Environment.NewLine}{Limit(result.Output + result.Error, 50000)}";
    }

    private string SafeWorkspacePath(string? relative, bool mustExist)
    {
        relative = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Workspace tool paths must be relative to Lumi Workstation.");
        var root = Path.GetFullPath(WorkstationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(WorkstationRoot, relative));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workspace path escaped the Lumi Workstation root and was blocked.");
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new FileNotFoundException("Workspace path does not exist.", candidate);
        return candidate;
    }

    private void WriteRuntimeLedger(string kind, object data)
    {
        try
        {
            Directory.CreateDirectory(RuntimeRoot);
            var row = JsonSerializer.Serialize(new { at = DateTimeOffset.Now, sessionId = _runtimeSessionId, kind, data });
            File.AppendAllText(RuntimeLedgerPath, row + Environment.NewLine);
        }
        catch { }
    }

    private static RuntimeDecision ParseRuntimeDecision(string raw)
    {
        raw = raw.Trim();
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new RuntimeDecision(raw, string.Empty, EmptyArgs(), true);

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var reply = root.TryGetProperty("reply", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var tool = root.TryGetProperty("tool", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var done = root.TryGetProperty("done", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False ? d.GetBoolean() : string.IsNullOrWhiteSpace(tool);
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : EmptyArgs();
            return new RuntimeDecision(reply, tool, args, done);
        }
        catch
        {
            return new RuntimeDecision(raw, string.Empty, EmptyArgs(), true);
        }
    }

    private static JsonElement EmptyArgs()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string? Arg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ExplicitDeployIntent(string text)
    {
        var s = text.ToLowerInvariant();
        return s.Contains("deploy") || s.Contains("install") || s.Contains("update lumi") || s.Contains("push to phone") || s.Contains("send to phone");
    }

    private static string Limit(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed record RuntimeDecision(string Reply, string Tool, JsonElement Args, bool Done);
}
