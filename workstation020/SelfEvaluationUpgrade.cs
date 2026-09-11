using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool SelfEvaluationHook = RegisterSelfEvaluationHook();
    private readonly SemaphoreSlim _selfEvaluationGate = new(1, 1);
    private const string CanonicalRepoUrl = "https://github.com/es831j-cell/Lumi-APK-Factory-Build.git";

    private static bool RegisterSelfEvaluationHook()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewKeyDownEvent, new KeyEventHandler(SelfEvaluation_PreviewKeyDown), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SelfEvaluation_PreviewMouseDown), true);
        return true;
    }

    private static async void SelfEvaluation_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window) return;
        if (Keyboard.FocusedElement != window.CommandInputBox) return;
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        var text = window.CommandInputBox.Text;
        if (!IsSelfEvaluationIntent(text)) return;
        e.Handled = true;
        await window.ExecuteSelfEvaluationIntentAsync(text);
    }

    private static async void SelfEvaluation_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MainWindow window || !window.SendCommandButton.IsMouseOver) return;
        var text = window.CommandInputBox.Text;
        if (!IsSelfEvaluationIntent(text)) return;
        e.Handled = true;
        await window.ExecuteSelfEvaluationIntentAsync(text);
    }

    private static bool IsSelfEvaluationIntent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim().ToLowerInvariant();
        return text.Contains("what is wrong with you") || text.Contains("what's wrong with you")
            || text.Contains("what is wrong with your code") || text.Contains("what's wrong with your code")
            || text.Contains("check yourself") || text.Contains("diagnose yourself") || text.Contains("audit yourself")
            || text.Contains("self audit") || text.Contains("self-audit") || text.Contains("self evaluate")
            || text.Contains("self-evaluate") || text.Contains("evaluate yourself") || text.Contains("fix yourself")
            || text.Contains("repair yourself") || text.Contains("self repair") || text.Contains("self-repair")
            || text.Contains("heal yourself") || text.Contains("commission source") || text.Contains("get your source")
            || text.Contains("load your source");
    }

    private static bool IsRepairIntent(string raw)
    {
        var text = raw.ToLowerInvariant();
        return text.Contains("fix") || text.Contains("repair") || text.Contains("heal")
            || text.Contains("commission source") || text.Contains("get your source") || text.Contains("load your source");
    }

    private async Task ExecuteSelfEvaluationIntentAsync(string raw)
    {
        var instruction = raw.Trim();
        if (string.IsNullOrWhiteSpace(instruction)) return;
        if (!await _selfEvaluationGate.WaitAsync(0))
        {
            AppendCommand("LUMI > A self-evaluation or repair cycle is already running.");
            return;
        }

        CommandInputBox.Clear();
        AppendCommand($"YOU > {instruction}");
        SaveInstruction(instruction, IsRepairIntent(instruction) ? "SELF_REPAIR_REQUESTED" : "SELF_AUDIT_REQUESTED");
        try
        {
            if (IsRepairIntent(instruction)) await RunSelfRepairCycleAsync(instruction);
            else await RunSelfAuditAsync(true);
        }
        catch (Exception ex)
        {
            AppendCommand("LUMI > Self-evaluation failed: " + ex.Message);
            AppendDiagnostic("Self-evaluation failure: " + ex);
            WriteRuntimeLedger("self.error", new { error = ex.ToString(), instruction });
            StatusText.Text = "Self-evaluation failed";
        }
        finally
        {
            _selfEvaluationGate.Release();
            CommandInputBox.Focus();
        }
    }

    private async Task<SelfAuditSnapshot> RunSelfAuditAsync(bool showInConsole)
    {
        StatusText.Text = "Lumi is evaluating herself...";
        var liveSource = Path.Combine(WorkstationRoot, "Live", "source");
        var sourceReady = File.Exists(Path.Combine(liveSource, "gradlew.bat"));
        var gitReady = Directory.Exists(Path.Combine(liveSource, ".git"));
        var runtimeReady = _lumiRuntimeReady && Directory.Exists(RuntimeRoot);
        var enabledProviders = _providerConnections.Count(x => x.Enabled);
        var phoneBound = !string.IsNullOrWhiteSpace(_deviceSerial);
        var dockManifest = ResolveDockManifest();
        var dockReady = dockManifest is not null && File.Exists(dockManifest);
        var studio = FindAndroidStudioPath();
        var diagnosticsRoot = Path.Combine(WorkstationRoot, "Diagnostics");
        var diagnosticsReady = Directory.Exists(diagnosticsRoot) && Directory.EnumerateFiles(diagnosticsRoot, "*", SearchOption.AllDirectories).Any();
        var recentRuntimeErrors = CountRecentRuntimeErrors();

        string gitStatus = "not commissioned";
        if (gitReady)
        {
            var result = await RunProcessAsync("git.exe", "status --short --branch", liveSource, 120000);
            gitStatus = result.ExitCode == 0 ? Clip(result.Output.Trim(), 4000) : "git status failed: " + Clip(result.Error, 1000);
        }

        var failures = new List<string>();
        var warnings = new List<string>();
        if (!runtimeReady) failures.Add("Windows runtime is not active.");
        if (enabledProviders == 0) warnings.Add("No enabled reasoning provider is configured; local tools still work.");
        if (!sourceReady) failures.Add("Buildable Lumi source is missing from Live\\source.");
        if (sourceReady && !gitReady) warnings.Add("Live source exists but has no local Git baseline.");
        if (!phoneBound) warnings.Add("No phone is currently bound; phone-only validation/deployment is unavailable.");
        if (phoneBound && !dockReady) warnings.Add("Phone is visible but no verified dock manifest was found.");
        if (studio is null) warnings.Add("Android Studio was not detected in known Windows locations.");
        if (!diagnosticsReady) warnings.Add("No workstation diagnostics capture is available yet.");
        if (recentRuntimeErrors > 0) warnings.Add($"Runtime ledger contains {recentRuntimeErrors} recent error/failure event(s).");

        var state = failures.Count == 0 ? (warnings.Count == 0 ? "PASS" : "PASS_WITH_WARNINGS") : "FAIL";
        var snapshot = new SelfAuditSnapshot(DateTimeOffset.Now, state, runtimeReady, LumiDesktopTools.Length, enabledProviders,
            phoneBound, dockReady, sourceReady, gitReady, studio, diagnosticsReady, recentRuntimeErrors, gitStatus, failures, warnings);

        var dir = Path.Combine(WorkstationRoot, "SelfAudit", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "self-audit.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        var text = FormatSelfAudit(snapshot, dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "self-audit.txt"), text);
        WriteRuntimeLedger("self.audit", new { snapshot.State, snapshot.Failures, snapshot.Warnings, reportFolder = dir });
        if (showInConsole) AppendCommand("LUMI > " + text);
        StatusText.Text = $"Self-audit {state}";
        return snapshot;
    }

    private async Task RunSelfRepairCycleAsync(string instruction)
    {
        StatusText.Text = "Lumi self-repair cycle starting...";
        AppendCommand("LUMI > I will inspect first, repair only evidence-backed failures, then evaluate again.");
        var before = await RunSelfAuditAsync(false);
        var actions = new List<string>();
        EnsureWorkstationDirectories(actions);

        if (before.PhoneBound && !before.DockReady)
        {
            await SyncLumiToWorkstationAsync(true);
            actions.Add(PhoneStateText.Text.Contains("DOCKED", StringComparison.OrdinalIgnoreCase)
                ? "PASS: refreshed phone snapshot." : "FAIL: phone snapshot refresh did not reach DOCKED state.");
        }

        if (!before.SourceReady)
        {
            var commission = await CommissionCanonicalSourceAsync();
            actions.Add(commission.Message);
        }

        if (File.Exists(Path.Combine(WorkstationRoot, "Live", "source", "gradlew.bat")))
        {
            AppendCommand("LUMI > Source is present. Running a local build as repair validation.");
            await BuildLiveSourceAsync(false);
            actions.Add(StatusText.Text.Contains("PASS", StringComparison.OrdinalIgnoreCase)
                ? "PASS: local Gradle build validated the working source."
                : "FAIL: local Gradle build did not pass; diagnostics contain the build failure.");
        }

        var after = await RunSelfAuditAsync(false);
        var summary = new StringBuilder();
        summary.AppendLine("SELF-REPAIR CYCLE");
        summary.AppendLine($"Before: {before.State}");
        foreach (var action in actions) summary.AppendLine("• " + action);
        summary.AppendLine($"After: {after.State}");
        if (after.Failures.Count > 0)
        {
            summary.AppendLine("Remaining failures:");
            foreach (var failure in after.Failures) summary.AppendLine("• " + failure);
        }
        if (after.Warnings.Count > 0)
        {
            summary.AppendLine("Warnings:");
            foreach (var warning in after.Warnings) summary.AppendLine("• " + warning);
        }
        AppendCommand("LUMI > " + summary.ToString().TrimEnd());
        WriteRuntimeLedger("self.repair", new { instruction, before = before.State, after = after.State, actions });
        SaveInstruction(instruction, after.Failures.Count == 0 ? "SELF_REPAIR_COMPLETED" : "SELF_REPAIR_BLOCKED");
        StatusText.Text = after.Failures.Count == 0 ? "Self-repair cycle complete" : "Self-repair blocked by remaining failure";
    }

    private void EnsureWorkstationDirectories(List<string> actions)
    {
        foreach (var dir in new[] { RuntimeRoot, Path.Combine(WorkstationRoot, "Diagnostics"), Path.Combine(WorkstationRoot, "SelfAudit"),
                     Path.Combine(WorkstationRoot, "Live"), Path.Combine(WorkstationRoot, "Canonical"), Path.Combine(WorkstationRoot, "RepairBackups") })
            Directory.CreateDirectory(dir);
        actions.Add("PASS: verified workstation runtime/diagnostics/audit/live/canonical/backup directories.");
    }

    private async Task<CommissionResult> CommissionCanonicalSourceAsync()
    {
        StatusText.Text = "Commissioning canonical Lumi source...";
        AppendCommand("LUMI > Buildable source is missing. I am attempting to commission the exact current canonical release source.");
        var git = FindExecutable("git.exe");
        if (git is null) return new CommissionResult(false, "BLOCKED: Git for Windows is not installed or not on PATH.");

        var canonicalRoot = Path.Combine(WorkstationRoot, "Canonical", "lumi-release");
        var liveRoot = Path.Combine(WorkstationRoot, "Live");
        var liveSource = Path.Combine(liveRoot, "source");
        var staging = Path.Combine(liveRoot, ".incoming-source-" + Guid.NewGuid().ToString("N"));
        var packageExtract = Path.Combine(liveRoot, ".package-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (!Directory.Exists(Path.Combine(canonicalRoot, ".git")))
            {
                TryDeleteDirectory(canonicalRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(canonicalRoot)!);
                var clone = await RunGitNonInteractiveAsync(git, $"clone --depth 1 --single-branch --branch lumi-release \"{CanonicalRepoUrl}\" \"{canonicalRoot}\"", WorkstationRoot, 8 * 60 * 1000);
                if (clone.ExitCode != 0)
                    return new CommissionResult(false, "BLOCKED: canonical GitHub source could not be cloned. Sign into GitHub/Git Credential Manager on this laptop, then ask Lumi to fix herself again. " + Clip(clone.Error + clone.Output, 900));
            }
            else
            {
                var fetch = await RunGitNonInteractiveAsync(git, "fetch --depth 1 origin lumi-release", canonicalRoot, 5 * 60 * 1000);
                if (fetch.ExitCode != 0) return new CommissionResult(false, "BLOCKED: canonical release fetch failed. " + Clip(fetch.Error + fetch.Output, 900));
                await RunGitNonInteractiveAsync(git, "checkout -f lumi-release", canonicalRoot, 120000);
                var reset = await RunGitNonInteractiveAsync(git, "reset --hard origin/lumi-release", canonicalRoot, 120000);
                if (reset.ExitCode != 0) return new CommissionResult(false, "BLOCKED: canonical release reset failed. " + Clip(reset.Error + reset.Output, 900));
            }

            var latestPath = Path.Combine(canonicalRoot, ".lumi", "latest-update.json");
            if (!File.Exists(latestPath)) return new CommissionResult(false, "BLOCKED: canonical release clone does not contain .lumi\\latest-update.json.");
            using var latestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(latestPath));
            var root = latestDoc.RootElement;
            var targetCode = root.TryGetProperty("targetVersionCode", out var codeEl) ? codeEl.GetInt32() : 0;
            var targetName = root.TryGetProperty("targetVersionName", out var nameEl) ? nameEl.GetString() ?? "unknown" : "unknown";
            var packageRelative = root.GetProperty("packagePath").GetString() ?? throw new InvalidOperationException("latest-update packagePath is blank.");
            var expectedSha = root.GetProperty("packageSha256").GetString() ?? throw new InvalidOperationException("latest-update packageSha256 is blank.");
            var packagePath = Path.Combine(canonicalRoot, packageRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(packagePath)) return new CommissionResult(false, "BLOCKED: published update package is missing: " + packageRelative);

            var actualSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))).ToLowerInvariant();
            if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                return new CommissionResult(false, $"BLOCKED: update package SHA mismatch. expected={expectedSha} actual={actualSha}");

            Directory.CreateDirectory(packageExtract);
            ZipFile.ExtractToDirectory(packagePath, packageExtract, true);
            var canonicalZip = Path.Combine(packageExtract, "payload", "canonical-source.zip");
            if (!File.Exists(canonicalZip)) return new CommissionResult(false, "BLOCKED: verified update package does not contain payload\\canonical-source.zip.");

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(canonicalZip, staging, true);
            var gradle = Directory.EnumerateFiles(staging, "gradlew.bat", SearchOption.AllDirectories).FirstOrDefault();
            if (gradle is null) return new CommissionResult(false, "BLOCKED: canonical-source.zip extracted but no gradlew.bat was found.");

            var prepared = Path.Combine(liveRoot, ".prepared-source-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(Path.GetDirectoryName(gradle)!, prepared);
            if (Directory.Exists(liveSource))
            {
                var backup = Path.Combine(WorkstationRoot, "RepairBackups", "source-before-commission-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(liveSource, backup);
            }
            Directory.Move(prepared, liveSource);

            var init = await RunGitNonInteractiveAsync(git, "init", liveSource, 120000);
            if (init.ExitCode == 0)
            {
                await RunGitNonInteractiveAsync(git, "config user.name \"Lumi Workstation\"", liveSource, 120000);
                await RunGitNonInteractiveAsync(git, "config user.email \"lumi@localhost\"", liveSource, 120000);
                await RunGitNonInteractiveAsync(git, "add -A", liveSource, 5 * 60 * 1000);
                await RunGitNonInteractiveAsync(git, $"commit -m \"Canonical Lumi Code {targetCode} baseline\"", liveSource, 5 * 60 * 1000);
            }

            var manifest = new { format = "LumiWorkstationSourceCommission", commissionedAt = DateTimeOffset.Now, source = CanonicalRepoUrl,
                branch = "lumi-release", targetVersionCode = targetCode, targetVersionName = targetName, packagePath = packageRelative,
                packageSha256 = actualSha, workingRoot = liveSource };
            await File.WriteAllTextAsync(Path.Combine(liveRoot, "source-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            ApplySourceTruth();
            WriteRuntimeLedger("source.commission", manifest);
            return new CommissionResult(true, $"PASS: commissioned canonical Lumi Code {targetCode} ({targetName}) into Live\\source and created a local Git baseline.");
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Canonical source commission failed: " + ex);
            return new CommissionResult(false, "BLOCKED: canonical source commission failed: " + ex.Message);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(packageExtract);
        }
    }

    private string? ResolveDockManifest()
    {
        if (!string.IsNullOrWhiteSpace(_lastDockFolder))
        {
            var direct = Path.Combine(_lastDockFolder, "dock-manifest.json");
            if (File.Exists(direct)) return direct;
        }
        var root = Path.Combine(WorkstationRoot, "DockedPhone");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "dock-manifest.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private int CountRecentRuntimeErrors()
    {
        try
        {
            if (!File.Exists(RuntimeLedger)) return 0;
            return File.ReadLines(RuntimeLedger).TakeLast(400).Count(line => line.Contains("runtime.error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("self.error", StringComparison.OrdinalIgnoreCase) || line.Contains("TOOL FAIL", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    private static string? FindExecutable(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(folder.Trim().Trim('"'), name); if (File.Exists(candidate)) return candidate; } catch { }
        }
        if (name.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "git.exe") };
            return candidates.FirstOrDefault(File.Exists);
        }
        return null;
    }

    private static string? FindAndroidStudioPath()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "bin", "studio64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Android Studio", "bin", "studio64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "apps", "AndroidStudio") };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(candidate))
            {
                var studio = Directory.EnumerateFiles(candidate, "studio64.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (studio is not null) return studio;
            }
        }
        return null;
    }

    private static async Task<ProcessResult> RunGitNonInteractiveAsync(string git, string arguments, string workingDirectory, int timeoutMs)
    {
        var psi = new ProcessStartInfo { FileName = git, Arguments = arguments, WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "Never";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException("Git operation exceeded the allowed run time."); }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string FormatSelfAudit(SelfAuditSnapshot s, string reportFolder)
    {
        var b = new StringBuilder();
        b.AppendLine($"SELF-AUDIT {s.State}");
        b.AppendLine($"PASS Runtime: {(s.RuntimeReady ? "ACTIVE" : "INACTIVE")} · tools={s.ToolCount}");
        b.AppendLine($"{(s.EnabledProviders > 0 ? "PASS" : "WARN")} Reasoning providers: {s.EnabledProviders} enabled");
        b.AppendLine($"{(s.PhoneBound ? "PASS" : "WARN")} Phone: {(s.PhoneBound ? "BOUND" : "NOT BOUND")}");
        b.AppendLine($"{(s.DockReady ? "PASS" : "WARN")} Dock snapshot: {(s.DockReady ? "VERIFIED" : "MISSING")}");
        b.AppendLine($"{(s.SourceReady ? "PASS" : "FAIL")} Live source: {(s.SourceReady ? "BUILDABLE" : "NOT COMMISSIONED")}");
        b.AppendLine($"{(s.GitReady ? "PASS" : "WARN")} Git baseline: {(s.GitReady ? "READY" : "MISSING")}");
        b.AppendLine($"{(s.AndroidStudioPath is not null ? "PASS" : "WARN")} Android Studio: {s.AndroidStudioPath ?? "NOT DETECTED"}");
        b.AppendLine($"{(s.DiagnosticsReady ? "PASS" : "WARN")} Diagnostics: {(s.DiagnosticsReady ? "AVAILABLE" : "NONE CAPTURED")}");
        b.AppendLine($"{(s.RecentRuntimeErrors == 0 ? "PASS" : "WARN")} Recent runtime errors: {s.RecentRuntimeErrors}");
        if (s.Failures.Count > 0) { b.AppendLine("Failures:"); foreach (var item in s.Failures) b.AppendLine("• " + item); }
        if (s.Warnings.Count > 0) { b.AppendLine("Warnings:"); foreach (var item in s.Warnings) b.AppendLine("• " + item); }
        b.AppendLine("Evidence: " + reportFolder);
        return b.ToString().TrimEnd();
    }

    private sealed record CommissionResult(bool Success, string Message);
    private sealed record SelfAuditSnapshot(DateTimeOffset EvaluatedAt, string State, bool RuntimeReady, int ToolCount, int EnabledProviders,
        bool PhoneBound, bool DockReady, bool SourceReady, bool GitReady, string? AndroidStudioPath, bool DiagnosticsReady,
        int RecentRuntimeErrors, string GitStatus, List<string> Failures, List<string> Warnings);
}
