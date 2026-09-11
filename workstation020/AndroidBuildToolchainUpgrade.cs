using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool AndroidToolchainHook = RegisterAndroidToolchainHook();
    private const string SigningEntropy = "LumiDockingStation.SigningVault.v1";

    private string SigningRoot => Path.Combine(VaultRoot, "Signing");
    private string SigningPropertiesProtected => Path.Combine(SigningRoot, "keystore-properties.dpapi");
    private string SigningKeystore => Path.Combine(SigningRoot, "lumi-signing.jks");

    private static bool RegisterAndroidToolchainHook()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(AndroidToolchain_Loaded), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(AndroidToolchain_PreviewKeyDown), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(AndroidToolchain_PreviewMouseDown), true);
        return true;
    }

    private static void AndroidToolchain_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.PrepareBundledAndroidToolchainEnvironment();
    }

    private void PrepareBundledAndroidToolchainEnvironment()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "toolchain");
        var gradleRoot = Path.Combine(root, "gradle");
        var gradleHome = Directory.Exists(gradleRoot)
            ? Directory.EnumerateDirectories(gradleRoot, "gradle-*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        var jdk = Path.Combine(root, "jdk");
        var sdk = Path.Combine(root, "android-sdk");

        if (gradleHome is not null && File.Exists(Path.Combine(gradleHome, "bin", "gradle.bat")))
        {
            Environment.SetEnvironmentVariable("LUMI_GRADLE_HOME", gradleHome);
            PrependPath(Path.Combine(gradleHome, "bin"));
        }
        if (File.Exists(Path.Combine(jdk, "bin", "java.exe")))
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", jdk);
            PrependPath(Path.Combine(jdk, "bin"));
        }
        if (File.Exists(Path.Combine(sdk, "platforms", "android-35", "android.jar")))
        {
            Environment.SetEnvironmentVariable("ANDROID_HOME", sdk);
            Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", sdk);
        }
        var gradleCache = Path.Combine(VaultRoot, "GradleCache");
        Directory.CreateDirectory(gradleCache);
        Environment.SetEnvironmentVariable("GRADLE_USER_HOME", gradleCache);

        AppendDiagnostic(IsAndroidToolchainReady()
            ? "Bundled Android build toolchain ready: Gradle 8.9 + JDK 17 + Android API 35."
            : "Bundled Android build toolchain is incomplete.");
    }

    private static void PrependPath(string folder)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(x => string.Equals(x.Trim().Trim('"'), folder, StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + current);
    }

    private bool IsAndroidToolchainReady()
    {
        var gradleHome = Environment.GetEnvironmentVariable("LUMI_GRADLE_HOME");
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (string.IsNullOrWhiteSpace(gradleHome) || !File.Exists(Path.Combine(gradleHome, "bin", "gradle.bat"))) return false;
        if (string.IsNullOrWhiteSpace(javaHome) || !File.Exists(Path.Combine(javaHome, "bin", "java.exe"))) return false;
        if (string.IsNullOrWhiteSpace(sdk) || !File.Exists(Path.Combine(sdk, "platforms", "android-35", "android.jar"))) return false;
        var buildTools = Path.Combine(sdk, "build-tools");
        return Directory.Exists(buildTools) && Directory.EnumerateDirectories(buildTools)
            .Any(x => File.Exists(Path.Combine(x, "aapt2.exe")) || File.Exists(Path.Combine(x, "aapt.exe")));
    }

    private bool IsSigningVaultReady() => File.Exists(SigningPropertiesProtected) && File.Exists(SigningKeystore);

    private void EnsureBundledGradleShim(string sourceRoot)
    {
        var shim = Path.Combine(sourceRoot, "gradlew.bat");
        if (!File.Exists(shim))
        {
            File.WriteAllText(shim,
                "@echo off\r\n" +
                "if not defined LUMI_GRADLE_HOME (echo LUMI_GRADLE_HOME is not configured.& exit /b 9009)\r\n" +
                "call \"%LUMI_GRADLE_HOME%\\bin\\gradle.bat\" %*\r\n" +
                "exit /b %ERRORLEVEL%\r\n",
                new UTF8Encoding(false));
        }
        var gitInfo = Path.Combine(sourceRoot, ".git", "info");
        if (Directory.Exists(Path.Combine(sourceRoot, ".git")))
        {
            Directory.CreateDirectory(gitInfo);
            var exclude = Path.Combine(gitInfo, "exclude");
            var text = File.Exists(exclude) ? File.ReadAllText(exclude) : string.Empty;
            if (!text.Contains("gradlew.bat", StringComparison.OrdinalIgnoreCase))
                File.AppendAllText(exclude, Environment.NewLine + "gradlew.bat" + Environment.NewLine);
        }
    }

    private async Task<CommissionResult> CommissionSigningVaultAsync(string canonicalRoot, string git)
    {
        if (IsSigningVaultReady()) return new CommissionResult(true, "PASS: signing vault already commissioned.");
        Directory.CreateDirectory(SigningRoot);
        var temp = Path.Combine(Path.GetTempPath(), "lumi-signing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var fetchMain = await RunGitNonInteractiveAsync(git, "fetch --depth 1 origin main", canonicalRoot, 5 * 60 * 1000);
            if (fetchMain.ExitCode != 0)
                return new CommissionResult(false, "BLOCKED: could not fetch trusted Factory signing source from origin/main. " + Clip(fetchMain.Error + fetchMain.Output, 800));

            var containerZip = Path.Combine(temp, "factory-container.zip");
            var archive = await RunGitNonInteractiveAsync(git,
                $"archive --format=zip --output=\"{containerZip}\" origin/main apkfactory-project.zip",
                canonicalRoot, 5 * 60 * 1000);
            if (archive.ExitCode != 0 || !File.Exists(containerZip))
                return new CommissionResult(false, "BLOCKED: trusted apkfactory-project.zip could not be extracted from origin/main. " + Clip(archive.Error + archive.Output, 800));

            ZipFile.ExtractToDirectory(containerZip, temp, true);
            var factoryZip = Path.Combine(temp, "apkfactory-project.zip");
            if (!File.Exists(factoryZip)) return new CommissionResult(false, "BLOCKED: Factory archive did not contain apkfactory-project.zip.");

            using var zip = ZipFile.OpenRead(factoryZip);
            var propsEntry = zip.GetEntry("app/keystore.properties");
            if (propsEntry is null) return new CommissionResult(false, "BLOCKED: trusted Factory ZIP is missing app/keystore.properties.");
            string propsText;
            using (var reader = new StreamReader(propsEntry.Open())) propsText = await reader.ReadToEndAsync();
            var storeMatch = Regex.Match(propsText, @"(?m)^\s*storeFile\s*=\s*(.+?)\s*$");
            if (!storeMatch.Success) return new CommissionResult(false, "BLOCKED: signing properties do not identify storeFile.");
            var storeName = Path.GetFileName(storeMatch.Groups[1].Value.Trim().Replace('\\', '/'));
            var storeEntry = zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, "app/" + storeName, StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(x => string.Equals(Path.GetFileName(x.FullName), storeName, StringComparison.OrdinalIgnoreCase));
            if (storeEntry is null) return new CommissionResult(false, "BLOCKED: signing keystore referenced by Factory properties was not found.");

            await using (var input = storeEntry.Open())
            await using (var output = File.Create(SigningKeystore))
                await input.CopyToAsync(output);

            var clear = Encoding.UTF8.GetBytes(propsText);
            var entropy = Encoding.UTF8.GetBytes(SigningEntropy);
            var protectedBytes = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(SigningPropertiesProtected, protectedBytes);
            var manifest = new
            {
                commissionedAt = DateTimeOffset.Now,
                source = "origin/main:apkfactory-project.zip",
                keystoreSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(SigningKeystore))).ToLowerInvariant(),
                protection = "Windows DPAPI CurrentUser",
                secretsPrinted = false
            };
            await File.WriteAllTextAsync(Path.Combine(SigningRoot, "signing-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return new CommissionResult(true, "PASS: trusted Lumi signing identity commissioned into the Windows DPAPI vault.");
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Signing vault commission failed: " + ex);
            return new CommissionResult(false, "BLOCKED: signing vault commission failed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private string StageSigningProperties(string sourceRoot)
    {
        if (!IsSigningVaultReady()) throw new InvalidOperationException("Lumi signing vault is not commissioned.");
        var protectedBytes = File.ReadAllBytes(SigningPropertiesProtected);
        var clear = ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes(SigningEntropy), DataProtectionScope.CurrentUser);
        var props = Encoding.UTF8.GetString(clear);
        var storePath = SigningKeystore.Replace('\\', '/');
        if (Regex.IsMatch(props, @"(?m)^\s*storeFile\s*="))
            props = Regex.Replace(props, @"(?m)^\s*storeFile\s*=.*$", "storeFile=" + storePath, 1);
        else props = "storeFile=" + storePath + Environment.NewLine + props;
        var target = Path.Combine(sourceRoot, "app", "keystore.properties");
        File.WriteAllText(target, props, new UTF8Encoding(false));
        return target;
    }

    private async Task BuildWithSigningAsync(bool deploy)
    {
        PrepareBundledAndroidToolchainEnvironment();
        var live = Path.Combine(WorkstationRoot, "Live", "source");
        if (!IsLumiAndroidSourceReady(live))
        {
            AppendCommand("LUMI > Build blocked: canonical Android source is not commissioned.");
            StatusText.Text = "Build blocked · source missing";
            return;
        }
        if (!IsAndroidToolchainReady())
        {
            AppendCommand("LUMI > Build blocked: bundled Android toolchain is incomplete. Expected Gradle 8.9, JDK 17, Android API 35 and Build Tools.");
            StatusText.Text = "Build blocked · toolchain missing";
            return;
        }
        if (!IsSigningVaultReady())
        {
            AppendCommand("LUMI > Build blocked: trusted Lumi signing vault is not commissioned.");
            StatusText.Text = "Build blocked · signing missing";
            return;
        }

        EnsureBundledGradleShim(live);
        string? staged = null;
        try
        {
            staged = StageSigningProperties(live);
            await BuildLiveSourceAsync(deploy);
        }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); } catch { }
            }
        }
    }

    private static bool IsBuildIntent(string? raw, out bool deploy)
    {
        deploy = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim().ToLowerInvariant();
        if (text is "build" or "build only") return true;
        if (text.Contains("deploy") || (text.Contains("build") && text.Contains("install"))) { deploy = true; return true; }
        return false;
    }

    private static async void AndroidToolchain_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || Keyboard.FocusedElement != window.CommandInputBox) return;
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (!IsBuildIntent(window.CommandInputBox.Text, out var deploy)) return;
        e.Handled = true;
        var command = window.CommandInputBox.Text.Trim();
        window.CommandInputBox.Clear();
        window.AppendCommand("YOU > " + command);
        await window.BuildWithSigningAsync(deploy);
    }

    private static async void AndroidToolchain_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MainWindow window) return;
        var button = VisualDescendants<Button>(window).FirstOrDefault(x => x.IsMouseOver);
        if (button is null) return;
        var label = ButtonLabel(button);
        bool? deploy = label.Equals("Build", StringComparison.OrdinalIgnoreCase) ? false
            : label.Equals("Install", StringComparison.OrdinalIgnoreCase) ? true : null;
        if (!deploy.HasValue) return;
        e.Handled = true;
        await window.BuildWithSigningAsync(deploy.Value);
    }
}
