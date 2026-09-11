using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool RuntimeHook = RegisterRuntimeHook();
    private bool _lumiRuntimeReady;
    private readonly string _runtimeSessionId = Guid.NewGuid().ToString("N");
    private string RuntimeRoot => Path.Combine(WorkstationRoot, "Runtime");
    private string RuntimeLedger => Path.Combine(RuntimeRoot, "runtime-ledger.jsonl");

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

    private static bool RegisterRuntimeHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_RuntimeLoaded),
            true);
        return true;
    }

    private static void MainWindow_RuntimeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window) window.InitializeLumiDesktopRuntime();
    }

    private void InitializeLumiDesktopRuntime()
    {
        if (_lumiRuntimeReady) return;
        _lumiRuntimeReady = true;
        Directory.CreateDirectory(RuntimeRoot);

        File.WriteAllText(
            Path.Combine(RuntimeRoot, "runtime-state.json"),
            JsonSerializer.Serialize(new
            {
                format = "LumiDesktopRuntimeState",
                formatVersion = 1,
                name = "Lumi",
                activeHost = "windows-workstation",
                sessionId = _runtimeSessionId,
                startedAt = DateTimeOffset.Now,
                toolRegistry = LumiDesktopTools,
                rule = "AI providers are reasoning engines. Lumi owns tool execution and evidence."
            }, new JsonSerializerOptions { WriteIndented = true }));

        CommandInputBox.PreviewKeyDown += LumiRuntime_PreviewKeyDown;
        SendCommandButton.PreviewMouseLeftButtonDown += LumiRuntime_PreviewMouseDown;
        WriteRuntimeLedger("runtime.start", new { activeHost = "windows-workstation", toolCount = LumiDesktopTools.Length });
        AppendCommand($"LUMI > Windows Runtime ACTIVE. {LumiDesktopTools.Length} workstation tools registered.");
        AppendDiagnostic($"Lumi Windows Runtime active. session={_runtimeSessionId}");
    }

    private async void LumiRuntime_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (!RuntimeOwnsPrompt(CommandInputBox.Text)) return;
        e.Handled = true;
        await RouteThroughLumiRuntimeAsync();
    }

    private async void LumiRuntime_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!RuntimeOwnsPrompt(CommandInputBox.Text)) return;
        e.Handled = true;
        await RouteThroughLumiRuntimeAsync();
    }

    private bool RuntimeOwnsPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsDockTransferCommand(text)) return false;
        if (IsLocalWorkstationCommand(text)) return false;
        return true;
    }

    private async Task RouteThroughLumiRuntimeAsync()
    {
        var instruction = CommandInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(instruction)) return;

        var provider = ChooseProviderForPrompt();
        if (provider is null)
        {
            CommandInputBox.Clear();
            AppendCommand($"YOU > {instruction}");
            AppendCommand("LUMI > My Windows runtime is active, but I need an enabled reasoning provider in AI Vault for open-ended reasoning. Local tools still work without an AI key.");
            SaveInstruction(instruction, "LUMI_RUNTIME_NO_PROVIDER");
            return;
        }

        await ExecuteLumiDesktopPromptAsync(provider, instruction);
    }

    private async Task ExecuteLumiDesktopPromptAsync(ProviderConnection provider, string instruction)
    {
        CommandInputBox.Clear();
        AppendCommand($"YOU > {instruction}");
        SaveInstruction(instruction, "LUMI_RUNTIME_RECEIVED");
        SendCommandButton.IsEnabled = false;
        StatusText.Text = $"Lumi reasoning via {provider.Name}...";
        WriteRuntimeLedger("instruction", new { instruction, provider = provider.Name });

        var evidence = new StringBuilder();
        try
        {
            for (var step = 1; step <= 6; step++)
            {
                var raw = await CallProviderAsync(provider, RuntimePlannerPrompt(instruction, evidence.ToString(), step));
                var decision = ParseDecision(raw);
                WriteRuntimeLedger("reasoning.decision", new { step, provider = provider.Name, decision.Tool, decision.Done, decision.Reply });

                if (decision.Done || string.IsNullOrWhiteSpace(decision.Tool))
                {
                    AppendCommand("LUMI > " + (string.IsNullOrWhiteSpace(decision.Reply) ? raw.Trim() : decision.Reply.Trim()));
                    SaveInstruction(instruction, "LUMI_RUNTIME_ANSWERED");
                    StatusText.Text = $"Lumi ready · {provider.Name}";
                    return;
                }

                StatusText.Text = $"Lumi using {decision.Tool}...";
                var result = await ExecuteLumiToolAsync(decision.Tool, decision.Args, instruction);
                evidence.AppendLine($"STEP {step}: {decision.Tool}");
                evidence.AppendLine(result);
                WriteRuntimeLedger("tool.result", new { step, tool = decision.Tool, result = Clip(result, 8000) });
                AppendDiagnostic($"Runtime tool {decision.Tool}: {Clip(result, 1500)}");
            }

            AppendCommand("LUMI > I reached my six-step tool limit for this instruction. Completed evidence is saved in the Runtime ledger.");
            StatusText.Text = "Lumi runtime step limit";
        }
        catch (Exception ex)
        {
            AppendCommand("LUMI > Windows runtime error: " + ex.Message);
            AppendDiagnostic("Lumi Windows runtime failure: " + ex);
            WriteRuntimeLedger("runtime.error", new { error = ex.ToString() });
            StatusText.Text = "Lumi runtime error";
        }
        finally
        {
            SendCommandButton.IsEnabled = true;
            CommandInputBox.Focus();
        }
    }

    private string RuntimePlannerPrompt(string instruction, string evidence, int step)
    {
        var schema = "{\"reply\":\"short text for Lumi to say\",\"tool\":\"tool.name or empty\",\"args\":{\"path\":\"optional\",\"content\":\"optional\"},\"done\":true}";
        var toolList = string.Join(Environment.NewLine, LumiDesktopTools.Select(x => "- " + x));
        var phone = string.IsNullOrWhiteSpace(_deviceSerial) ? "not-bound" : _deviceSerial;
        var sourceReady = File.Exists(Path.Combine(WorkstationRoot, "Live", "source", "gradlew.bat"));

        return
            "You are a reasoning engine assisting Lumi Desktop Runtime. You are NOT Lumi and you do NOT execute tools." + Environment.NewLine +
            "Lumi is the persistent assistant/orchestrator running on Windows. Lumi owns local actions and evidence." + Environment.NewLine +
            "Return exactly one JSON object and no markdown." + Environment.NewLine +
            "Schema: " + schema + Environment.NewLine +
            "If a real action is needed, request exactly one registered tool and set done=false. Never claim an action happened before tool evidence proves it." + Environment.NewLine +
            "Prefer inspection before modification. lumi.deploy is allowed only when the original user wording explicitly requests deploy/install/update." + Environment.NewLine +
            "Finish with tool=empty and done=true when enough evidence exists." + Environment.NewLine + Environment.NewLine +
            "TOOLS:" + Environment.NewLine + toolList + Environment.NewLine + Environment.NewLine +
            $"RUNTIME: host=windows-workstation; session={_runtimeSessionId}; phone={phone}; liveSource={(sourceReady ? "ready" : "missing")}; step={step}/6" + Environment.NewLine +
            "USER INSTRUCTION:" + Environment.NewLine + instruction + Environment.NewLine + Environment.NewLine +
            "TOOL EVIDENCE:" + Environment.NewLine + (string.IsNullOrWhiteSpace(evidence) ? "(none)" : Clip(evidence, 12000));
    }

    private async Task<string> ExecuteLumiToolAsync(string requestedTool, JsonElement args, string originalInstruction)
    {
        var tool = requestedTool.Trim().ToLowerInvariant();
        if (!LumiDesktopTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            return $"TOOL FAIL: {tool} is not registered.";

        switch (tool)
        {
            case "runtime.status":
                return RuntimeStatus();
            case "lumi.sync_phone":
                await SyncLumiToWorkstationAsync(true);
                return $"SYNC: {PhoneStateText.Text}; {StatusText.Text}; folder={_lastDockFolder ?? "none"}";
            case "lumi.phone_status":
                await RefreshPhoneAsync();
                return $"PHONE: {PhoneStateText.Text}; {PhoneDetailText.Text.Replace(Environment.NewLine, " | ")}";
            case "lumi.diagnostics":
                await CaptureDiagnosticsAsync();
                return "DIAGNOSTICS: " + StatusText.Text;
            case "lumi.build":
                await BuildLiveSourceAsync(false);
                return "BUILD: " + StatusText.Text;
            case "lumi.deploy":
                if (!ExplicitDeployIntent(originalInstruction)) return "DEPLOY BLOCKED: user did not explicitly request install/deploy/update.";
                await BuildLiveSourceAsync(true);
                return "DEPLOY: " + StatusText.Text;
            case "android_studio.open":
                OpenAndroidStudio();
                return "ANDROID STUDIO: " + StatusText.Text;
            case "workspace.list":
                return WorkspaceList(Arg(args, "path"));
            case "workspace.read":
                return WorkspaceRead(Arg(args, "path"));
            case "workspace.write":
                return WorkspaceWrite(Arg(args, "path"), Arg(args, "content"), false);
            case "workspace.append":
                return WorkspaceWrite(Arg(args, "path"), Arg(args, "content"), true);
            case "git.status":
                return await GitTool("status --short --branch");
            case "git.diff":
                return await GitTool("diff --stat");
            case "git.log":
                return await GitTool("log -8 --oneline --decorate");
            default:
                return "TOOL FAIL: unhandled tool.";
        }
    }

    private string RuntimeStatus()
    {
        var live = Path.Combine(WorkstationRoot, "Live", "source", "gradlew.bat");
        return $"RUNTIME ACTIVE; host=windows-workstation; session={_runtimeSessionId}; tools={LumiDesktopTools.Length}; phone={_deviceSerial ?? "none"}; dockFolder={_lastDockFolder ?? "none"}; liveSource={(File.Exists(live) ? "ready" : "missing")}; providers={_providerConnections.Count}.";
    }

    private string WorkspaceList(string? relative)
    {
        var dir = SafeWorkspacePath(relative, false);
        if (!Directory.Exists(dir)) return "LIST FAIL: directory not found.";
        var rows = Directory.EnumerateFileSystemEntries(dir).Take(200)
            .Select(p => (Directory.Exists(p) ? "DIR  " : "FILE ") + Path.GetFileName(p));
        return "LIST PASS: " + Path.GetRelativePath(WorkstationRoot, dir) + Environment.NewLine + string.Join(Environment.NewLine, rows);
    }

    private string WorkspaceRead(string? relative)
    {
        var path = SafeWorkspacePath(relative, true);
        if (Directory.Exists(path)) return "READ FAIL: path is a directory.";
        var info = new FileInfo(path);
        if (info.Length > 2_000_000) return $"READ FAIL: file is {info.Length} bytes; limit is 2 MB.";
        return $"READ PASS: {Path.GetRelativePath(WorkstationRoot, path)}{Environment.NewLine}{Clip(File.ReadAllText(path), 50000)}";
    }

    private string WorkspaceWrite(string? relative, string? content, bool append)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "WRITE FAIL: path required.";
        content ??= string.Empty;
        if (content.Length > 250_000) return "WRITE FAIL: content exceeds 250,000 characters.";
        var path = SafeWorkspacePath(relative, false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (append) File.AppendAllText(path, content); else File.WriteAllText(path, content);
        return $"WRITE PASS: {(append ? "appended" : "wrote")} {content.Length} characters to {Path.GetRelativePath(WorkstationRoot, path)}";
    }

    private async Task<string> GitTool(string arguments)
    {
        var repo = Path.Combine(WorkstationRoot, "Live", "source");
        if (!Directory.Exists(Path.Combine(repo, ".git"))) return "GIT FAIL: live source repository is not commissioned.";
        var result = await RunProcessAsync("git.exe", arguments, repo, 120000);
        return $"GIT EXIT {result.ExitCode}{Environment.NewLine}{Clip(result.Output + result.Error, 50000)}";
    }

    private string SafeWorkspacePath(string? relative, bool mustExist)
    {
        relative = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Workspace paths must be relative.");
        var root = Path.GetFullPath(WorkstationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(WorkstationRoot, relative));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workspace path escaped Lumi Workstation and was blocked.");
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate)) throw new FileNotFoundException("Workspace path not found.", candidate);
        return candidate;
    }

    private void WriteRuntimeLedger(string kind, object data)
    {
        try
        {
            Directory.CreateDirectory(RuntimeRoot);
            File.AppendAllText(RuntimeLedger, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, sessionId = _runtimeSessionId, kind, data }) + Environment.NewLine);
        }
        catch { }
    }

    private static RuntimeDecision ParseDecision(string raw)
    {
        raw = raw.Trim();
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return new RuntimeDecision(raw, string.Empty, EmptyArgs(), true);
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var reply = root.TryGetProperty("reply", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var tool = root.TryGetProperty("tool", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var done = root.TryGetProperty("done", out var d) && (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False) ? d.GetBoolean() : string.IsNullOrWhiteSpace(tool);
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
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ExplicitDeployIntent(string text)
    {
        var s = text.ToLowerInvariant();
        return s.Contains("deploy") || s.Contains("install") || s.Contains("update lumi") || s.Contains("push to phone") || s.Contains("send to phone");
    }

    private static string Clip(string? text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed record RuntimeDecision(string Reply, string Tool, JsonElement Args, bool Done);
}
