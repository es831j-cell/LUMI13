using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private string? _adbPath;
    private string? _deviceSerial;

    private string WorkstationRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Lumi Workstation");

    private string VaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DistressedElk", "LumiDockingStation");

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(WorkstationRoot);
        Directory.CreateDirectory(VaultRoot);
        StatusText.Text = "Command deck online";
        CommandInputBox.Focus();
        await RefreshPhoneAsync();
    }

    private async void SendCommand_Click(object sender, RoutedEventArgs e) => await ExecuteCommandAsync();

    private async void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await ExecuteCommandAsync();
        }
    }

    private async Task ExecuteCommandAsync()
    {
        var text = CommandInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        CommandInputBox.Clear();
        AppendCommand($"YOU > {text}");
        SaveInstruction(text, "RECEIVED");
        var cmd = text.ToLowerInvariant();

        if (cmd is "help" or "?")
        {
            AppendCommand("LUMI > Direct commands: check phone, status, diagnostics, build, deploy, android studio, workspace. Anything else is treated as a development instruction.");
            return;
        }

        if (cmd.Contains("check phone") || cmd.Contains("connect phone") || cmd is "connect" or "refresh")
        {
            await RefreshPhoneAsync();
            return;
        }

        if (cmd is "status" || cmd.Contains("phone status"))
        {
            await RefreshPhoneAsync();
            AppendCommand($"LUMI > {PhoneStateText.Text}. {PhoneDetailText.Text.Replace(Environment.NewLine, " | ")}");
            return;
        }

        if (cmd.Contains("diagnostic") || cmd.Contains("logcat") || cmd.Contains("black box"))
        {
            await CaptureDiagnosticsAsync();
            return;
        }

        if (cmd == "build" || cmd.StartsWith("build only"))
        {
            await BuildLiveSourceAsync(false);
            return;
        }

        if (cmd.Contains("deploy") || (cmd.Contains("build") && cmd.Contains("install")))
        {
            await BuildLiveSourceAsync(true);
            return;
        }

        if (cmd.Contains("android studio"))
        {
            OpenAndroidStudio();
            return;
        }

        if (cmd.Contains("workspace"))
        {
            OpenWorkspace();
            return;
        }

        await AnswerDevelopmentInstructionAsync(text);
    }

    private async Task AnswerDevelopmentInstructionAsync(string instruction)
    {
        var groq = LoadSecret("groq");
        var openRouter = LoadSecret("openrouter");

        string? endpoint = null;
        string? model = null;
        string? key = null;
        string? provider = null;

        if (!string.IsNullOrWhiteSpace(groq))
        {
            provider = "Groq";
            key = groq;
            endpoint = "https://api.groq.com/openai/v1/chat/completions";
            model = "openai/gpt-oss-20b";
        }
        else if (!string.IsNullOrWhiteSpace(openRouter))
        {
            provider = "OpenRouter";
            key = openRouter;
            endpoint = "https://openrouter.ai/api/v1/chat/completions";
            model = "openrouter/free";
        }

        if (provider is null)
        {
            AppendCommand("LUMI > Instruction recorded locally. Add a Groq or OpenRouter key under API Keys if you want an AI response here. Direct workstation commands do not need a cloud key.");
            SaveInstruction(instruction, "QUEUED_NO_AI");
            StatusText.Text = "Instruction recorded";
            return;
        }

        SendCommandButton.IsEnabled = false;
        StatusText.Text = $"Thinking via {provider}...";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (provider == "OpenRouter")
                req.Headers.TryAddWithoutValidation("X-Title", "Lumi Docking Station");

            var payload = new
            {
                model,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You are the engineering copilot inside Lumi Docking Station. Be concise and concrete. Never claim a file, build, phone, repo, or deployment was changed unless the workstation actually executed that local action. Help move Lumi toward Release 1.0."
                    },
                    new { role = "user", content = instruction }
                },
                temperature = 0.2
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"{provider} HTTP {(int)res.StatusCode}: {Short(body)}");

            using var doc = JsonDocument.Parse(body);
            var choices = doc.RootElement.GetProperty("choices");
            var answer = choices.GetArrayLength() > 0
                ? choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim()
                : null;

            AppendCommand($"LUMI [{provider}] > {answer ?? "No response text returned."}");
            SaveInstruction(instruction, $"ANSWERED_{provider.ToUpperInvariant()}");
            StatusText.Text = $"Answered via {provider}";
        }
        catch (Exception ex)
        {
            AppendCommand($"LUMI > AI request failed: {ex.Message}");
            AppendDiagnostic($"AI request failed: {ex.Message}");
            SaveInstruction(instruction, "AI_ERROR");
            StatusText.Text = "AI request failed";
        }
        finally
        {
            SendCommandButton.IsEnabled = true;
            CommandInputBox.Focus();
        }
    }

    private async void RefreshPhone_Click(object sender, RoutedEventArgs e) => await RefreshPhoneAsync();

    private async Task RefreshPhoneAsync()
    {
        PhoneStateText.Text = "Checking...";
        PhoneDetailText.Text = "Looking for Android Debug Bridge and an authorized phone.";
        TopConnectionText.Text = "PHONE: CHECKING";
        StatusText.Text = "Checking phone...";

        _adbPath ??= FindAdb();
        if (_adbPath is null)
        {
            _deviceSerial = null;
            PhoneStateText.Text = "ADB NOT FOUND";
            PhoneDetailText.Text = "Install Android Studio or Android platform-tools. The workstation will detect adb.exe automatically afterward.";
            TopConnectionText.Text = "PHONE: ADB MISSING";
            StatusText.Text = "ADB not found";
            AppendDiagnostic("ADB not found in PATH, ANDROID_SDK_ROOT, ANDROID_HOME, or LocalAppData\\Android\\Sdk.");
            return;
        }

        try
        {
            await RunAdbAsync("start-server");
            var devices = await RunAdbAsync("devices -l");
            var lines = devices.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !x.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lines.Any(x => x.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
            {
                _deviceSerial = null;
                PhoneStateText.Text = "USB DEBUGGING NOT AUTHORIZED";
                PhoneDetailText.Text = "Unlock the phone and approve 'Allow USB debugging?', then press Connect / Refresh.";
                TopConnectionText.Text = "PHONE: AUTHORIZE";
                StatusText.Text = "Phone found, authorization required";
                return;
            }

            var authorized = lines.Where(x => Regex.IsMatch(x, @"^\S+\s+device(\s|$)")).ToList();
            if (authorized.Count == 0)
            {
                _deviceSerial = null;
                PhoneStateText.Text = "PHONE NOT FOUND";
                PhoneDetailText.Text = "Use a data-capable USB cable and enable Developer options > USB debugging on the phone.";
                TopConnectionText.Text = "PHONE: NOT FOUND";
                StatusText.Text = "No authorized Android phone";
                return;
            }

            if (authorized.Count > 1)
            {
                _deviceSerial = null;
                PhoneStateText.Text = "MULTIPLE ADB DEVICES";
                PhoneDetailText.Text = "Disconnect extra phones or emulators so the docking station can bind to one Lumi phone.";
                TopConnectionText.Text = "PHONE: MULTIPLE";
                StatusText.Text = "Multiple ADB devices";
                return;
            }

            _deviceSerial = authorized[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            var model = (await RunAdbAsync($"-s {_deviceSerial} shell getprop ro.product.model")).Output.Trim();
            var android = (await RunAdbAsync($"-s {_deviceSerial} shell getprop ro.build.version.release")).Output.Trim();
            var package = await RunAdbAsync($"-s {_deviceSerial} shell dumpsys package com.distressedelk.lumi", throwOnFailure: false);
            var versionName = Regex.Match(package.Output, @"versionName=([^\r\n]+)").Groups[1].Value.Trim();
            var versionCode = Regex.Match(package.Output, @"versionCode=(\d+)").Groups[1].Value.Trim();
            var lumiFound = !string.IsNullOrWhiteSpace(versionCode) || !string.IsNullOrWhiteSpace(versionName);

            PhoneStateText.Text = lumiFound ? "CONNECTED · LUMI DETECTED" : "CONNECTED · LUMI NOT FOUND";
            PhoneDetailText.Text = $"Device: {model}{Environment.NewLine}Android: {android}{Environment.NewLine}ADB: Authorized{Environment.NewLine}Serial: {_deviceSerial}" +
                (lumiFound ? $"{Environment.NewLine}Lumi: Code {versionCode} · {versionName}" : "");
            TopConnectionText.Text = lumiFound ? "PHONE: CONNECTED" : "PHONE: CONNECTED / NO LUMI";
            StatusText.Text = lumiFound ? $"Connected · Lumi Code {versionCode}" : "Phone connected · Lumi package not detected";
            AppendCommand($"LUMI > Phone connected: {model}, Android {android}" + (lumiFound ? $", Lumi Code {versionCode} ({versionName})." : "."));
            AppendDiagnostic($"ADB connected: model={model}, Android={android}, serial={_deviceSerial}, LumiCode={versionCode}, LumiVersion={versionName}");
        }
        catch (Exception ex)
        {
            _deviceSerial = null;
            PhoneStateText.Text = "PHONE CHECK FAILED";
            PhoneDetailText.Text = ex.Message;
            TopConnectionText.Text = "PHONE: ERROR";
            StatusText.Text = "Phone check failed";
            AppendDiagnostic("ADB phone check failed: " + ex.Message);
        }
    }

    private async void OpenLumi_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsurePhoneAsync()) return;
        try
        {
            await RunAdbAsync($"-s {_deviceSerial} shell monkey -p com.distressedelk.lumi -c android.intent.category.LAUNCHER 1");
            AppendCommand("LUMI > Opened Lumi on the connected phone.");
            StatusText.Text = "Lumi opened on phone";
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Could not open Lumi: " + ex.Message);
        }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await CaptureDiagnosticsAsync();

    private async Task CaptureDiagnosticsAsync()
    {
        if (!await EnsurePhoneAsync()) return;
        try
        {
            var dir = Path.Combine(WorkstationRoot, "Diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            StatusText.Text = "Capturing phone diagnostics...";

            var props = await RunAdbAsync($"-s {_deviceSerial} shell getprop", false);
            var package = await RunAdbAsync($"-s {_deviceSerial} shell dumpsys package com.distressedelk.lumi", false);
            var activity = await RunAdbAsync($"-s {_deviceSerial} shell dumpsys activity processes", false);
            var logcat = await RunAdbAsync($"-s {_deviceSerial} logcat -d -t 3000", false);

            await File.WriteAllTextAsync(Path.Combine(dir, "device-properties.txt"), props.Output + props.Error);
            await File.WriteAllTextAsync(Path.Combine(dir, "lumi-package.txt"), package.Output + package.Error);
            await File.WriteAllTextAsync(Path.Combine(dir, "activity-processes.txt"), activity.Output + activity.Error);
            await File.WriteAllTextAsync(Path.Combine(dir, "logcat.txt"), logcat.Output + logcat.Error);

            AppendCommand($"LUMI > Diagnostics captured to {dir}");
            AppendDiagnostic($"Diagnostics capture complete: {dir}");
            StatusText.Text = "Diagnostics captured";
        }
        catch (Exception ex)
        {
            AppendCommand("LUMI > Diagnostics failed: " + ex.Message);
            AppendDiagnostic("Diagnostics failed: " + ex.Message);
            StatusText.Text = "Diagnostics failed";
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildLiveSourceAsync(false);
    private async void Deploy_Click(object sender, RoutedEventArgs e) => await BuildLiveSourceAsync(true);

    private async Task BuildLiveSourceAsync(bool deploy)
    {
        var live = Path.Combine(WorkstationRoot, "Live", "source");
        var gradle = Path.Combine(live, "gradlew.bat");
        if (!File.Exists(gradle))
        {
            AppendCommand($"LUMI > Live source is not commissioned yet. Expected: {gradle}");
            StatusText.Text = "Live source missing";
            OpenWorkspace();
            return;
        }

        if (deploy && !await EnsurePhoneAsync()) return;

        try
        {
            StatusText.Text = deploy ? "Building Lumi for deployment..." : "Building Lumi...";
            AppendCommand($"LUMI > Starting {(deploy ? "build + install" : "build-only")} from {live}");
            var build = await RunProcessAsync(gradle, "assembleDebug", live, 20 * 60 * 1000);
            AppendDiagnostic("Gradle output:\n" + Short(build.Output, 5000));
            if (build.ExitCode != 0)
                throw new InvalidOperationException("Gradle build failed. Open Diagnostics for details.");

            AppendCommand("LUMI > Build PASS.");
            StatusText.Text = "Build PASS";
            if (!deploy) return;

            var apk = Directory.GetFiles(Path.Combine(live, "app", "build", "outputs", "apk"), "*.apk", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (apk is null) throw new FileNotFoundException("Build passed but no APK was found.");

            var install = await RunAdbAsync($"-s {_deviceSerial} install -r \"{apk}\"", false);
            if (install.ExitCode != 0 || !install.Output.Contains("Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ADB replace-in-place install failed: " + Short(install.Output + install.Error));

            AppendCommand($"LUMI > Deployment PASS. Installed {Path.GetFileName(apk)} with adb install -r. Lumi was not uninstalled.");
            StatusText.Text = "Deployment PASS";
            await RefreshPhoneAsync();
        }
        catch (Exception ex)
        {
            AppendCommand("LUMI > " + (deploy ? "Deployment" : "Build") + " failed: " + ex.Message);
            AppendDiagnostic((deploy ? "Deployment" : "Build") + " failed: " + ex);
            StatusText.Text = deploy ? "Deployment failed" : "Build failed";
        }
    }

    private void OpenAndroidStudio_Click(object sender, RoutedEventArgs e) => OpenAndroidStudio();

    private void OpenAndroidStudio()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "bin", "studio64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Android Studio", "bin", "studio64.exe")
        };
        var studio = candidates.FirstOrDefault(File.Exists);
        if (studio is null)
        {
            AppendCommand("LUMI > Android Studio was not detected in the standard Windows locations.");
            StatusText.Text = "Android Studio not detected";
            return;
        }

        var live = Path.Combine(WorkstationRoot, "Live", "source");
        var args = Directory.Exists(live) ? $"\"{live}\"" : string.Empty;
        Process.Start(new ProcessStartInfo { FileName = studio, Arguments = args, UseShellExecute = true });
        AppendCommand("LUMI > Android Studio opened.");
        StatusText.Text = "Android Studio opened";
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e) => OpenWorkspace();

    private void OpenWorkspace()
    {
        Directory.CreateDirectory(WorkstationRoot);
        Process.Start(new ProcessStartInfo { FileName = WorkstationRoot, UseShellExecute = true });
        AppendCommand($"LUMI > Opened {WorkstationRoot}");
        StatusText.Text = "Workspace opened";
    }

    private void SaveGroq_Click(object sender, RoutedEventArgs e)
    {
        SaveSecret("groq", GroqKeyBox.Password);
        GroqKeyBox.Clear();
    }

    private void SaveOpenRouter_Click(object sender, RoutedEventArgs e)
    {
        SaveSecret("openrouter", OpenRouterKeyBox.Password);
        OpenRouterKeyBox.Clear();
    }

    private void SaveSecret(string provider, string value)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            AppendCommand($"LUMI > {provider} key was blank, nothing saved.");
            return;
        }

        try
        {
            var clear = Encoding.UTF8.GetBytes(value);
            var entropy = Encoding.UTF8.GetBytes("LumiDockingStation.v020");
            var encrypted = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(VaultRoot, provider + ".key"), encrypted);
            AppendCommand($"LUMI > {provider} key encrypted to this Windows account.");
            StatusText.Text = $"{provider} key saved";
        }
        catch (Exception ex)
        {
            AppendDiagnostic($"Could not save {provider} key: {ex.Message}");
            StatusText.Text = "Key save failed";
        }
    }

    private string? LoadSecret(string provider)
    {
        try
        {
            var path = Path.Combine(VaultRoot, provider + ".key");
            if (!File.Exists(path)) return null;
            var entropy = Encoding.UTF8.GetBytes("LumiDockingStation.v020");
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception ex)
        {
            AppendDiagnostic($"Could not read {provider} key: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> EnsurePhoneAsync()
    {
        if (_deviceSerial is not null) return true;
        await RefreshPhoneAsync();
        return _deviceSerial is not null;
    }

    private static string? FindAdb()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var candidate = Path.Combine(root, "platform-tools", "adb.exe");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim(), "adb.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private async Task<ProcessResult> RunAdbAsync(string arguments, bool throwOnFailure = true)
    {
        if (_adbPath is null) throw new InvalidOperationException("ADB is not installed or was not detected.");
        var result = await RunProcessAsync(_adbPath, arguments, null, 120000);
        if (throwOnFailure && result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
        return result;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string? workingDirectory, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
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
            throw new TimeoutException($"{Path.GetFileName(fileName)} exceeded the allowed run time.");
        }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private void SaveInstruction(string instruction, string state)
    {
        try
        {
            var dir = Path.Combine(WorkstationRoot, "Instructions");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "instruction-ledger.txt"),
                $"{DateTimeOffset.Now:O}\t{state}\t{instruction.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Could not write instruction ledger: " + ex.Message);
        }
    }

    private void AppendCommand(string line)
    {
        CommandTranscriptBox.AppendText((CommandTranscriptBox.Text.Length == 0 ? string.Empty : Environment.NewLine) + $"[{DateTime.Now:HH:mm:ss}] {line}");
        CommandTranscriptBox.ScrollToEnd();
    }

    private void AppendDiagnostic(string line)
    {
        DiagnosticsBox.AppendText((DiagnosticsBox.Text.Length == 0 ? string.Empty : Environment.NewLine) + $"[{DateTime.Now:HH:mm:ss}] {line}");
        DiagnosticsBox.ScrollToEnd();
    }

    private static string Short(string value, int max = 500)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value[..max] + "...";
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
