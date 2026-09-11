using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool SelfEvaluationV2Hook = RegisterSelfEvaluationV2Hook();
    private readonly SemaphoreSlim _selfEvaluationGate = new(1, 1);
    private const string CanonicalRepoUrl = "https://github.com/es831j-cell/Lumi-APK-Factory-Build.git";

    private static bool RegisterSelfEvaluationV2Hook()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(SelfEvaluationV2_PreviewKeyDown), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SelfEvaluationV2_PreviewMouseDown), true);
        return true;
    }

    private static async void SelfEvaluationV2_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || Keyboard.FocusedElement != window.CommandInputBox) return;
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        var text = window.CommandInputBox.Text;
        if (!IsSelfEvaluationIntentV2(text)) return;
        e.Handled = true;
        await window.ExecuteSelfEvaluationIntentV2Async(text);
    }

    private static async void SelfEvaluationV2_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MainWindow window || !window.SendCommandButton.IsMouseOver) return;
        var text = window.CommandInputBox.Text;
        if (!IsSelfEvaluationIntentV2(text)) return;
        e.Handled = true;
        await window.ExecuteSelfEvaluationIntentV2Async(text);
    }

    private static bool IsSelfEvaluationIntentV2(string? raw)
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

    private static bool IsRepairIntentV2(string raw)
    {
        var text = raw.ToLowerInvariant();
        return text.Contains("fix") || text.Contains("repair") || text.Contains("heal")
            || text.Contains("commission source") || text.Contains("get your source") || text.Contains("load your source");
    }

    private async Task ExecuteSelfEvaluationIntentV2Async(string raw)
    {
        var instruction = raw.Trim();
        if (string.IsNullOrWhiteSpace(instruction)) return;
        if (!await _selfEvaluationGate.WaitAsync(0))
        {
            AppendCommand("LUMI > A self-evaluation or repair cycle is already running.");
            return;
        }

        CommandInputBox.Clear();
        AppendCommand("YOU > " + instruction);
        SaveInstruction(instruction, IsRepairIntentV2(instruction) ? "SELF_REPAIR_REQUESTED" : "SELF_AUDIT_REQUESTED");
        try
        {
            if (IsRepairIntentV2(instruction)) await RunSelfRepairCycleAsync(instruction);
            else await RunSelfAuditV2Async(true);
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

    private bool IsLumiAndroidSourceReady(string root)
    {
        return (File.Exists(Path.Combine(root, "build.gradle")) || File.Exists(Path.Combine(root, "build.gradle.kts")))
            && (File.Exists(Path.Combine(root, "app", "build.gradle")) || File.Exists(Path.Combine(root, "app", "build.gradle.kts")))
            && Directory.Exists(Path.Combine(root, "app", "src", "main"));
    }

    private async Task<SelfAuditSnapshotV2> RunSelfAuditV2Async(bool showInConsole)
    {
        StatusText.Text = "Lumi is evaluating herself...";
        PrepareBundledAndroidToolchainEnvironment();
        var liveSource = Path.Combine(WorkstationRoot, "Live", "source");
        var sourceReady = IsLumiAndroidSourceReady(liveSource);
        var gitReady = Directory.Exists(Path.Combine(liveSource, ".git"));
        var toolchainReady = IsAndroidToolchainReady();
        var signingReady = IsSigningVaultReady();
        var runtimeReady = _lumiRuntimeReady && Directory.Exists(RuntimeRoot);
        var enabledProviders = _providerConnections.Count(x => x.Enabled);
        var phoneBound = !string.IsNullOrWhiteSpace(_deviceSerial);
        var dockManifest = ResolveDockManifestV2();
        var dockReady = dockManifest is not null && File.Exists(dockManifest);
        var studio = FindAndroidStudioPath();
        var diagnosticsRoot = Path.Combine(WorkstationRoot, "Diagnostics");
        var diagnosticsReady = Directory.Exists(diagnosticsRoot) && Directory.EnumerateFiles(diagnosticsRoot, "*", SearchOption.AllDirectories).Any();
        var recentRuntimeErrors = CountRecentRuntimeErrorsV2();

        string gitStatus = "not commissioned";
        if (gitReady)
        {
            var git = FindExecutable("git.exe");
            if (git is not null)
            {
                var result = await RunGitNonInteractiveAsync(git, "status --short --branch", liveSource, 120000);
                gitStatus = result.ExitCode == 0 ? Clip(result.Output.Trim(), 4000) : "git status failed: " + Clip(result.Error, 1000);
            }
        }

        var failures = new List<string>();
        var warnings = new List<string>();
        if (!runtimeReady) failures.Add("Windows runtime is not active.");
        if (!sourceReady) failures.Add("Canonical Lumi Android source is missing from Live\\source.");
        if (sourceReady && !gitReady) warnings.Add("Live source exists but has no local Git baseline.");
        if (!toolchainReady) failures.Add("Windows Android build toolchain is incomplete (Gradle 8.9, JDK 17, Android API 35/build-tools required).");
        if (!signingReady) failures.Add("Trusted Lumi signing identity is not commissioned into the Windows DPAPI vault.");
        if (enabledProviders == 0) warnings.Add("No enabled reasoning provider is configured; local tools still work.");
        if (!phoneBound) warnings.Add("No phone is currently bound; phone-only validation/deployment is unavailable.");
        if (phoneBound && !dockReady) warnings.Add("Phone is visible but no verified dock manifest was found.");
        if (studio is null) warnings.Add("Android Studio IDE was not detected. This is not a build blocker when the bundled toolchain is ready.");
        if (!diagnosticsReady) warnings.Add("No workstation diagnostics capture is available yet.");
        if (recentRuntimeErrors > 0) warnings.Add($"Runtime ledger contains {recentRuntimeErrors} recent error/failure event(s).");

        var state = failures.Count == 0 ? (warnings.Count == 0 ? "PASS" : "PASS_WITH_WARNINGS") : "FAIL";
        var snapshot = new SelfAuditSnapshotV2(DateTimeOffset.Now, state, runtimeReady, LumiDesktopTools.Length, enabledProviders,
            phoneBound, dockReady, sourceReady, gitReady, toolchainReady, signingReady, studio, diagnosticsReady,
            recentRuntimeErrors, gitStatus, failures, warnings);

        var dir = Path.Combine(WorkstationRoot, "SelfAudit", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "self-audit.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        var text = FormatSelfAuditV2(snapshot, dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "self-audit.txt"), text);
        WriteRuntimeLedger("self.audit", new { snapshot.State, snapshot.Failures, snapshot.Warnings, reportFolder = dir });
        if (showInConsole) AppendCommand("LUMI > " + text);
        StatusText.Text = "Self-audit " + state;
        return snapshot;
    }

    private async Task RunSelfRepairCycleAsync(string instruction)
    {
        StatusText.Text = "Lumi self-repair cycle starting...";
        AppendCommand("LUMI > I will inspect first, repair only evidence-backed failures, then evaluate again.");
        var before = await RunSelfAuditV2Async(false);
        var actions = new List<string>();
        EnsureWorkstationDirectoriesV2(actions);

        if (before.PhoneBound && !before.DockReady)
        {
            await SyncLumiToWorkstationAsync(true);
            actions.Add(PhoneStateText.Text.Contains("DOCKED", StringComparison.OrdinalIgnoreCase)
                ? "PASS: refreshed phone snapshot." : "FAIL: phone snapshot refresh did not reach DOCKED state.");
        }

        var liveSource = Path.Combine(WorkstationRoot, "Live", "source");
        if (!before.SourceReady)
        {
            var commission = await CommissionCanonicalSourceAsync();
            actions.Add(commission.Message);
        }

        PrepareBundledAndroidToolchainEnvironment();
        if (IsLumiAndroidSourceReady(liveSource) && !IsSigningVaultReady())
        {
            var git = FindExecutable("git.exe");
            var canonicalRoot = Path.Combine(WorkstationRoot, "Canonical", "lumi-release");
            if (git is not null && Directory.Exists(Path.Combine(canonicalRoot, ".git")))
            {
                var signing = await CommissionSigningVaultAsync(canonicalRoot, git);
                actions.Add(signing.Message);
            }
        }

        if (IsLumiAndroidSourceReady(liveSource) && IsAndroidToolchainReady() && IsSigningVaultReady())
        {
            AppendCommand("LUMI > Source, toolchain and signing identity are ready. Running a real local build as repair validation.");
            await BuildWithSigningAsync(false);
            actions.Add(StatusText.Text.Contains("PASS", StringComparison.OrdinalIgnoreCase)
                ? "PASS: local Gradle build validated the commissioned source."
                : "FAIL: local Gradle build did not pass; diagnostics contain the build failure.");
        }
        else
        {
            if (!IsAndroidToolchainReady()) actions.Add("BLOCKED: bundled Android build toolchain is incomplete.");
            if (!IsSigningVaultReady()) actions.Add("BLOCKED: trusted Lumi signing identity is not commissioned.");
        }

        var after = await RunSelfAuditV2Async(false);
        var summary = new StringBuilder();
        summary.AppendLine("SELF-REPAIR CYCLE");
        summary.AppendLine("Before: " + before.State);
        foreach (var action in actions) summary.AppendLine("• " + action);
        summary.AppendLine("After: " + after.State);
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

    private void EnsureWorkstationDirectoriesV2(List<string> actions)
    {
        foreach (var dir in new[] { RuntimeRoot, Path.Combine(WorkstationRoot, "Diagnostics"), Path.Combine(WorkstationRoot, "SelfAudit"),
                     Path.Combine(WorkstationRoot, "Live"), Path.Combine(WorkstationRoot, "Canonical"), Path.Combine(WorkstationRoot, "RepairBackups") })
            Directory.CreateDirectory(dir);
        actions.Add("PASS: verified workstation runtime/diagnostics/audit/live/canonical/backup directories.");
    }

    private async Task<CommissionResult> CommissionCanonicalSourceAsync()
    {
        StatusText.Text = "Commissioning canonical Lumi source...";
        AppendCommand("LUMI > Canonical Android source is missing. I am commissioning the exact current release source by project structure, not by Gradle-wrapper presence.");
        PrepareBundledGitEnvironment();
        var git = FindExecutable("git.exe");
        if (git is null) return new CommissionResult(false, "BLOCKED: bundled Git runtime is unavailable.");

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
                var clone = await RunGitNonInteractiveAsync(git,
                    $"clone --depth 1 --single-branch --branch lumi-release \"{CanonicalRepoUrl}\" \"{canonicalRoot}\"",
                    WorkstationRoot, 8 * 60 * 1000);
                if (clone.ExitCode != 0)
                    return new CommissionResult(false, "BLOCKED: canonical GitHub source could not be cloned. " + Clip(clone.Error + clone.Output, 1200));
            }
            else
            {
                var fetch = await RunGitNonInteractiveAsync(git, "fetch --depth 1 origin lumi-release", canonicalRoot, 5 * 60 * 1000);
                if (fetch.ExitCode != 0) return new CommissionResult(false, "BLOCKED: canonical release fetch failed. " + Clip(fetch.Error + fetch.Output, 1000));
                await RunGitNonInteractiveAsync(git, "checkout -f lumi-release", canonicalRoot, 120000);
                var reset = await RunGitNonInteractiveAsync(git, "reset --hard origin/lumi-release", canonicalRoot, 120000);
                if (reset.ExitCode != 0) return new CommissionResult(false, "BLOCKED: canonical release reset failed. " + Clip(reset.Error + reset.Output, 1000));
            }

            var latestPath = Path.Combine(canonicalRoot, ".lumi", "latest-update.json");
            if (!File.Exists(latestPath)) return new CommissionResult(false, "BLOCKED: lumi-release does not contain .lumi\\latest-update.json.");
            using var latestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(latestPath));
            var root = latestDoc.RootElement;
            var targetCode = root.GetProperty("targetVersionCode").GetInt32();
            var targetName = root.GetProperty("targetVersionName").GetString() ?? "unknown";
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
            var appBuild = Directory.EnumerateFiles(staging, "build.gradle", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(staging, "build.gradle.kts", SearchOption.AllDirectories))
                .FirstOrDefault(x => string.Equals(Path.GetFileName(Path.GetDirectoryName(x)), "app", StringComparison.OrdinalIgnoreCase));
            if (appBuild is null)
                return new CommissionResult(false, "BLOCKED: canonical-source.zip does not contain an Android app/build.gradle project.");
            var appDir = Path.GetDirectoryName(appBuild)!;
            var projectRoot = Directory.GetParent(appDir)?.FullName;
            if (projectRoot is null || !IsLumiAndroidSourceReady(projectRoot))
                return new CommissionResult(false, "BLOCKED: canonical archive Android project root could not be resolved.");

            var buildText = await File.ReadAllTextAsync(appBuild);
            var codeMatch = Regex.Match(buildText, @"\bversionCode\s+(\d+)");
            if (!codeMatch.Success || !int.TryParse(codeMatch.Groups[1].Value, out var sourceCode) || sourceCode != targetCode)
                return new CommissionResult(false, $"BLOCKED: canonical source identity mismatch. release={targetCode}, source={codeMatch.Groups[1].Value}.");
            if (!buildText.Contains(targetName, StringComparison.Ordinal))
                return new CommissionResult(false, "BLOCKED: canonical source versionName does not match the published release pointer.");

            var prepared = Path.Combine(liveRoot, ".prepared-source-" + Guid.NewGuid().ToString("N"));
            CopyDirectoryV2(projectRoot, prepared);
            if (Directory.Exists(liveSource))
            {
                var backup = Path.Combine(WorkstationRoot, "RepairBackups", "source-before-commission-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(liveSource, backup);
            }
            Directory.Move(prepared, liveSource);

            var init = await RunGitNonInteractiveAsync(git, "init", liveSource, 120000);
            if (init.ExitCode != 0) return new CommissionResult(false, "BLOCKED: local Git baseline initialization failed. " + Clip(init.Error, 800));
            await RunGitNonInteractiveAsync(git, "config user.name \"Lumi Workstation\"", liveSource, 120000);
            await RunGitNonInteractiveAsync(git, "config user.email \"lumi@localhost\"", liveSource, 120000);
            await RunGitNonInteractiveAsync(git, "add -A", liveSource, 5 * 60 * 1000);
            var commit = await RunGitNonInteractiveAsync(git, $"commit -m \"Canonical Lumi Code {targetCode} baseline\"", liveSource, 5 * 60 * 1000);
            if (commit.ExitCode != 0) return new CommissionResult(false, "BLOCKED: local canonical baseline commit failed. " + Clip(commit.Error + commit.Output, 800));

            EnsureBundledGradleShim(liveSource);
            var signing = await CommissionSigningVaultAsync(canonicalRoot, git);

            var manifest = new
            {
                format = "LumiWorkstationSourceCommissionV2",
                commissionedAt = DateTimeOffset.Now,
                source = CanonicalRepoUrl,
                branch = "lumi-release",
                targetVersionCode = targetCode,
                targetVersionName = targetName,
                packagePath = packageRelative,
                packageSha256 = actualSha,
                projectIdentity = "app/build.gradle",
                wrapperPolicy = "workstation shim uses bundled Gradle 8.9",
                workingRoot = liveSource,
                signingVaultReady = signing.Success
            };
            await File.WriteAllTextAsync(Path.Combine(liveRoot, "source-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            ApplySourceTruth();
            WriteRuntimeLedger("source.commission", manifest);
            var message = $"PASS: commissioned canonical Lumi Code {targetCode} ({targetName}) into Live\\source and created a local Git baseline.";
            if (!signing.Success) message += " " + signing.Message;
            else message += " Signing vault PASS.";
            return new CommissionResult(true, message);
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

    private string? ResolveDockManifestV2()
    {
        if (!string.IsNullOrWhiteSpace(_lastDockFolder))
        {
            var direct = Path.Combine(_lastDockFolder, "dock-manifest.json");
            if (File.Exists(direct)) return direct;
        }
        var root = Path.Combine(WorkstationRoot, "DockedPhone");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "dock-manifest.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private int CountRecentRuntimeErrorsV2()
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
        if (name.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
            if (File.Exists(bundled)) return bundled;
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        if (name.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "git.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }
        return null;
    }

    private static string? FindAndroidStudioPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "bin", "studio64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Android Studio", "bin", "studio64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "apps", "AndroidStudio")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(candidate))
            {
                try
                {
                    var studio = Directory.EnumerateFiles(candidate, "studio64.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (studio is not null) return studio;
                }
                catch { }
            }
        }
        return null;
    }

    private static async Task<ProcessResult> RunGitNonInteractiveAsync(string git, string arguments, string workingDirectory, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = git,
            Arguments = "-c credential.helper=manager " + arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "Never";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("Git operation exceeded the allowed run time.");
        }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static void CopyDirectoryV2(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectoryV2(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string FormatSelfAuditV2(SelfAuditSnapshotV2 s, string reportFolder)
    {
        var b = new StringBuilder();
        b.AppendLine("SELF-AUDIT " + s.State);
        b.AppendLine($"{(s.RuntimeReady ? "PASS" : "FAIL")} Runtime: {(s.RuntimeReady ? "ACTIVE" : "INACTIVE")} · tools={s.ToolCount}");
        b.AppendLine($"{(s.EnabledProviders > 0 ? "PASS" : "WARN")} Reasoning providers: {s.EnabledProviders} enabled");
        b.AppendLine($"{(s.PhoneBound ? "PASS" : "WARN")} Phone: {(s.PhoneBound ? "BOUND" : "NOT BOUND")}");
        b.AppendLine($"{(s.DockReady ? "PASS" : "WARN")} Dock snapshot: {(s.DockReady ? "VERIFIED" : "MISSING")}");
        b.AppendLine($"{(s.SourceReady ? "PASS" : "FAIL")} Live source: {(s.SourceReady ? "CANONICAL ANDROID PROJECT" : "NOT COMMISSIONED")}");
        b.AppendLine($"{(s.GitReady ? "PASS" : "WARN")} Git baseline: {(s.GitReady ? "READY" : "MISSING")}");
        b.AppendLine($"{(s.ToolchainReady ? "PASS" : "FAIL")} Build toolchain: {(s.ToolchainReady ? "GRADLE 8.9 · JDK 17 · API 35 READY" : "INCOMPLETE")}");
        b.AppendLine($"{(s.SigningReady ? "PASS" : "FAIL")} Signing vault: {(s.SigningReady ? "DPAPI PROTECTED · READY" : "NOT COMMISSIONED")}");
        b.AppendLine($"{(s.AndroidStudioPath is not null ? "PASS" : "WARN")} Android Studio IDE: {s.AndroidStudioPath ?? "NOT DETECTED (optional for CLI build)"}");
        b.AppendLine($"{(s.DiagnosticsReady ? "PASS" : "WARN")} Diagnostics: {(s.DiagnosticsReady ? "AVAILABLE" : "NONE CAPTURED")}");
        b.AppendLine($"{(s.RecentRuntimeErrors == 0 ? "PASS" : "WARN")} Recent runtime errors: {s.RecentRuntimeErrors}");
        if (s.Failures.Count > 0) { b.AppendLine("Failures:"); foreach (var item in s.Failures) b.AppendLine("• " + item); }
        if (s.Warnings.Count > 0) { b.AppendLine("Warnings:"); foreach (var item in s.Warnings) b.AppendLine("• " + item); }
        b.AppendLine("Evidence: " + reportFolder);
        return b.ToString().TrimEnd();
    }

    private sealed record CommissionResult(bool Success, string Message);
    private sealed record SelfAuditSnapshotV2(DateTimeOffset EvaluatedAt, string State, bool RuntimeReady, int ToolCount, int EnabledProviders,
        bool PhoneBound, bool DockReady, bool SourceReady, bool GitReady, bool ToolchainReady, bool SigningReady,
        string? AndroidStudioPath, bool DiagnosticsReady, int RecentRuntimeErrors, string GitStatus,
        List<string> Failures, List<string> Warnings);
}
