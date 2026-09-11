using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool GitHubRepairResumeHook = RegisterGitHubRepairResumeHook();
    private bool _githubRepairResumeScheduled;

    private static bool RegisterGitHubRepairResumeHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GitHubRepairResume_Loaded),
            true);
        return true;
    }

    private static void GitHubRepairResume_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.CommandTranscriptBox.TextChanged -= window.GitHubRepairResume_TranscriptChanged;
        window.CommandTranscriptBox.TextChanged += window.GitHubRepairResume_TranscriptChanged;
    }

    private void GitHubRepairResume_TranscriptChanged(object sender, TextChangedEventArgs e)
    {
        if (_githubRepairResumeScheduled) return;
        var text = CommandTranscriptBox.Text;
        if (!text.Contains("canonical GitHub source could not be cloned", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("could not read Username for 'https://github.com'", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase))
            return;

        _githubRepairResumeScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () => await AuthorizeGitHubAndResumeRepairAsync()));
    }

    private async Task AuthorizeGitHubAndResumeRepairAsync()
    {
        try
        {
            PrepareBundledGitEnvironment();
            var git = FindExecutable("git.exe");
            var gcm = FindGitCredentialManager();
            if (git is null || gcm is null)
            {
                AppendCommand("LUMI > AUTO-REPAIR BLOCKED: bundled Git or Git Credential Manager is missing. Reinstall the current Workstation build.");
                StatusText.Text = "GitHub authorization tool missing";
                return;
            }

            AppendCommand("LUMI > GitHub authorization is required for my private canonical source. I am opening the secure browser sign-in now. Approve it once; I will resume this repair automatically.");
            StatusText.Text = "Waiting for GitHub authorization...";
            WriteRuntimeLedger("github.auth.required", new { reason = "canonical-source-self-repair", git, credentialManager = gcm });

            var interactive = await RunGitWithCredentialManagerInteractiveAsync(
                git,
                $"ls-remote --heads \"{CanonicalRepoUrl}\" lumi-release",
                WorkstationRoot,
                10 * 60 * 1000);

            if (interactive.ExitCode != 0)
            {
                var detail = Clip(interactive.Error + interactive.Output, 1400);
                AppendCommand("LUMI > GITHUB AUTH FAIL. Browser authorization did not establish repository access. " + detail);
                StatusText.Text = "GitHub authorization failed";
                WriteRuntimeLedger("github.auth", new { state = "FAIL", detail });
                return;
            }

            var verify = await RunGitNonInteractiveAsync(
                git,
                $"-c credential.helper=manager ls-remote --heads \"{CanonicalRepoUrl}\" lumi-release",
                WorkstationRoot,
                120000);

            if (verify.ExitCode != 0 || !verify.Output.Contains("refs/heads/lumi-release", StringComparison.OrdinalIgnoreCase))
            {
                var detail = Clip(verify.Error + verify.Output, 1400);
                AppendCommand("LUMI > GITHUB AUTH FAIL. Authorization completed, but I still cannot verify the lumi-release branch. " + detail);
                StatusText.Text = "GitHub repository access failed";
                WriteRuntimeLedger("github.auth", new { state = "FAIL_VERIFY", detail });
                return;
            }

            AppendCommand("LUMI > GITHUB AUTH PASS. Private canonical source access verified. Resuming self-repair automatically.");
            StatusText.Text = "GitHub authorized · resuming repair";
            WriteRuntimeLedger("github.auth", new { state = "PASS", autoResume = true });

            await _selfEvaluationGate.WaitAsync();
            try
            {
                await RunSelfRepairCycleAsync("automatic resume after GitHub authorization");
            }
            finally
            {
                _selfEvaluationGate.Release();
            }
        }
        catch (Exception ex)
        {
            AppendCommand("LUMI > GitHub authorization/resume failed: " + ex.Message);
            AppendDiagnostic("GitHub authorization/resume failure: " + ex);
            WriteRuntimeLedger("github.auth.error", new { error = ex.ToString() });
            StatusText.Text = "GitHub authorization/resume failed";
        }
        finally
        {
            _githubRepairResumeScheduled = false;
        }
    }

    private static async Task<ProcessResult> RunGitWithCredentialManagerInteractiveAsync(
        string git,
        string arguments,
        string workingDirectory,
        int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = git,
            Arguments = $"-c credential.helper=manager -c credential.interactive=always {arguments}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "1";
        psi.Environment["GCM_INTERACTIVE"] = "Always";
        psi.Environment["GCM_GUI_PROMPT"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Git authentication flow.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("GitHub browser authorization exceeded the allowed time.");
        }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}
