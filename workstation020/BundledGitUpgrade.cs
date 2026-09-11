using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool BundledGitHook = RegisterBundledGitHook();

    private static bool RegisterBundledGitHook()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(BundledGit_Loaded), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewKeyDownEvent, new KeyEventHandler(BundledGit_PreviewKeyDown), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(BundledGit_PreviewMouseDown), true);
        return true;
    }

    private static void BundledGit_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.PrepareBundledGitEnvironment();
    }

    private void PrepareBundledGitEnvironment()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "git");
        var git = Path.Combine(root, "cmd", "git.exe");
        if (!File.Exists(git))
        {
            AppendDiagnostic("Bundled Git runtime not found under application directory.");
            return;
        }

        var additions = new[]
        {
            Path.Combine(root, "cmd"),
            Path.Combine(root, "bin"),
            Path.Combine(root, "mingw64", "bin"),
            Path.Combine(root, "usr", "bin")
        }.Where(Directory.Exists);

        var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var merged = string.Join(Path.PathSeparator, additions) + Path.PathSeparator + existing;
        Environment.SetEnvironmentVariable("PATH", merged);
        AppendDiagnostic("Bundled Git ready: " + git);
    }

    private static bool IsGitHubConnectIntent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim().ToLowerInvariant();
        return text is "connect github" or "github login" or "login github" or "authenticate github" or "sign in github" or "sign into github";
    }

    private static async void BundledGit_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || Keyboard.FocusedElement != window.CommandInputBox) return;
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (!IsGitHubConnectIntent(window.CommandInputBox.Text)) return;
        e.Handled = true;
        await window.ConnectGitHubAsync(window.CommandInputBox.Text.Trim());
    }

    private static async void BundledGit_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MainWindow window || !window.SendCommandButton.IsMouseOver) return;
        if (!IsGitHubConnectIntent(window.CommandInputBox.Text)) return;
        e.Handled = true;
        await window.ConnectGitHubAsync(window.CommandInputBox.Text.Trim());
    }

    private async Task ConnectGitHubAsync(string instruction)
    {
        CommandInputBox.Clear();
        AppendCommand("YOU > " + instruction);
        SaveInstruction(instruction, "GITHUB_AUTH_REQUESTED");
        PrepareBundledGitEnvironment();

        var git = FindExecutable("git.exe");
        if (git is null)
        {
            AppendCommand("LUMI > GitHub connection failed because no Git runtime is available. This build is expected to carry Portable Git; reinstall the current Workstation build if this persists.");
            StatusText.Text = "Bundled Git missing";
            return;
        }

        var gcm = FindGitCredentialManager();
        if (gcm is null)
        {
            AppendCommand("LUMI > Git is available, but Git Credential Manager is missing from the bundled toolchain.");
            StatusText.Text = "Git Credential Manager missing";
            return;
        }

        AppendCommand("LUMI > Opening GitHub authentication. Complete the Git Credential Manager / browser sign-in, then I will verify access to my private canonical repository.");
        StatusText.Text = "Waiting for GitHub authentication...";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gcm,
                Arguments = "github login",
                UseShellExecute = true,
                WorkingDirectory = WorkstationRoot
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not launch Git Credential Manager.");
            await process.WaitForExitAsync();

            var verify = await RunGitNonInteractiveAsync(git, $"ls-remote --heads \"{CanonicalRepoUrl}\" lumi-release", WorkstationRoot, 120000);
            if (verify.ExitCode == 0 && verify.Output.Contains("refs/heads/lumi-release", StringComparison.OrdinalIgnoreCase))
            {
                AppendCommand("LUMI > GITHUB AUTH PASS. I can access my private canonical release repository. Run 'fix yourself' again and I will commission the source.");
                SaveInstruction(instruction, "GITHUB_AUTH_PASS");
                StatusText.Text = "GitHub authentication PASS";
                WriteRuntimeLedger("github.auth", new { state = "PASS", git, credentialManager = gcm });
            }
            else
            {
                var detail = Clip(verify.Error + verify.Output, 1200);
                AppendCommand("LUMI > GITHUB AUTH FAIL. Git Credential Manager completed, but repository access is still unavailable. " + detail);
                SaveInstruction(instruction, "GITHUB_AUTH_FAIL");
                StatusText.Text = "GitHub authentication failed";
                WriteRuntimeLedger("github.auth", new { state = "FAIL", detail });
            }
        }
        catch (Exception ex)
        {
            AppendCommand("LUMI > GitHub authentication failed: " + ex.Message);
            AppendDiagnostic("GitHub authentication failure: " + ex);
            SaveInstruction(instruction, "GITHUB_AUTH_ERROR");
            StatusText.Text = "GitHub authentication failed";
        }
    }

    private static string? FindGitCredentialManager()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "git"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git")
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var match = Directory.EnumerateFiles(root, "git-credential-manager.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null) return match;
            }
            catch { }
        }
        return null;
    }
}
