using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool DockUpgradeClassHook = RegisterDockUpgradeClassHook();
    private readonly SemaphoreSlim _dockSyncGate = new(1, 1);
    private bool _dockUpgradeInitialized;
    private string? _lastDockFolder;

    private static bool RegisterDockUpgradeClassHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_DockUpgradeLoaded),
            true);
        return true;
    }

    private static void MainWindow_DockUpgradeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeDockUpgrade();
    }

    private void InitializeDockUpgrade()
    {
        if (_dockUpgradeInitialized) return;
        _dockUpgradeInitialized = true;

        MinWidth = 980;
        MinHeight = 680;
        WindowState = WindowState.Maximized;

        ApplyResponsiveLayoutAndTruth();
        AddDockTransferControls();

        CommandInputBox.PreviewKeyDown += DockCommand_PreviewKeyDown;
        SendCommandButton.PreviewMouseLeftButtonDown += DockCommand_PreviewMouseDown;

        foreach (var button in VisualDescendants<Button>(this).ToList())
        {
            var label = ButtonLabel(button);
            if (label.Contains("Check Phone", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Refresh", StringComparison.OrdinalIgnoreCase)
                || label.Contains("Refresh Phone", StringComparison.OrdinalIgnoreCase))
            {
                button.Click += DockRefreshAfter_Click;
            }
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(async () =>
            {
                await Task.Delay(1100);
                await SyncLumiToWorkstationAsync(userRequested: false);
            }));
    }

    private void ApplyResponsiveLayoutAndTruth()
    {
        var version = WorkstationVersion();

        foreach (var text in VisualDescendants<TextBlock>(this))
        {
            if (text.Text.StartsWith("v0.3.", StringComparison.OrdinalIgnoreCase))
                text.Text = $"v{version}";
            else if (text.Text.StartsWith("Dock 0.3.", StringComparison.OrdinalIgnoreCase))
                text.Text = $"Dock {version}";
        }

        if (CommandTranscriptBox.Text.StartsWith("Lumi Workstation 0.3.", StringComparison.OrdinalIgnoreCase))
        {
            var firstBreak = CommandTranscriptBox.Text.IndexOf('\n');
            var rest = firstBreak >= 0 ? CommandTranscriptBox.Text[(firstBreak + 1)..] : string.Empty;
            CommandTranscriptBox.Text = $"Lumi Workstation {version} online.{Environment.NewLine}{rest}";
        }

        if (CommandInputBox.Parent is Grid commandRow && commandRow.Parent is Grid deckGrid && deckGrid.RowDefinitions.Count >= 5)
        {
            deckGrid.RowDefinitions[2].Height = GridLength.Auto;
            deckGrid.RowDefinitions[3].Height = new GridLength(66);
            deckGrid.RowDefinitions[4].Height = new GridLength(245);
        }

        ApplySourceTruth();
    }

    private void ApplySourceTruth(int? syncedPhoneFiles = null)
    {
        var liveGradle = Path.Combine(WorkstationRoot, "Live", "source", "gradlew.bat");
        var sourceReady = File.Exists(liveGradle);

        foreach (var text in VisualDescendants<TextBlock>(this))
        {
            if (text.Text.Contains("Canonical Source", StringComparison.OrdinalIgnoreCase))
                text.Text = "●   Canonical Source        Remote release reference";
            else if (text.Text.Contains("Live Source (Working)", StringComparison.OrdinalIgnoreCase))
                text.Text = sourceReady
                    ? "●   Live Source (Working)   Ready on workstation"
                    : "○   Live Source             NOT COMMISSIONED";
            else if (text.Text.Contains("Build Status", StringComparison.OrdinalIgnoreCase))
                text.Text = sourceReady
                    ? "●   Build Status            Ready"
                    : "○   Build Status            BLOCKED: source missing";
        }

        if (syncedPhoneFiles.HasValue)
        {
            var projectHeader = VisualDescendants<TextBlock>(this)
                .FirstOrDefault(x => x.Text.Equals("PROJECT STATUS", StringComparison.OrdinalIgnoreCase));
            if (projectHeader?.Parent is StackPanel panel)
            {
                var existing = panel.Children.OfType<TextBlock>()
                    .FirstOrDefault(x => x.Tag?.ToString() == "dock-sync-truth");
                if (existing is null)
                {
                    existing = new TextBlock
                    {
                        Tag = "dock-sync-truth",
                        Margin = new Thickness(0, 6, 0, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(73, 229, 142))
                    };
                    panel.Children.Add(existing);
                }
                existing.Text = $"●   Phone Snapshot          {syncedPhoneFiles.Value} file(s) synced";
            }
        }
    }

    private void AddDockTransferControls()
    {
        if (PhoneDetailText.Parent is not StackPanel panel) return;
        var wrap = panel.Children.OfType<WrapPanel>().FirstOrDefault();
        if (wrap is null) return;
        if (wrap.Children.OfType<Button>().Any(x => ButtonLabel(x).Contains("Sync Lumi", StringComparison.OrdinalIgnoreCase))) return;

        var sync = new Button
        {
            Content = "Sync Lumi → Workstation",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        sync.Click += async (_, _) => await SyncLumiToWorkstationAsync(userRequested: true);

        var open = new Button
        {
            Content = "Open Sync Folder",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        open.Click += (_, _) => OpenDockFolder();

        wrap.Children.Insert(0, sync);
        wrap.Children.Insert(1, open);
    }

    private async void DockRefreshAfter_Click(object sender, RoutedEventArgs e)
    {
        await Task.Delay(900);
        await SyncLumiToWorkstationAsync(userRequested: false);
    }

    private async void DockCommand_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (!IsDockTransferCommand(CommandInputBox.Text)) return;
        e.Handled = true;
        await ExecuteDockTransferCommandAsync();
    }

    private async void DockCommand_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDockTransferCommand(CommandInputBox.Text)) return;
        e.Handled = true;
        await ExecuteDockTransferCommandAsync();
    }

    private static bool IsDockTransferCommand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var cmd = raw.Trim().ToLowerInvariant();
        return cmd.Contains("check lumi")
            || cmd.Contains("sync lumi")
            || cmd.Contains("dock lumi")
            || cmd.Contains("transfer lumi")
            || cmd.Contains("pull lumi")
            || cmd.Equals("sync", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("dock", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExecuteDockTransferCommandAsync()
    {
        var command = CommandInputBox.Text.Trim();
        CommandInputBox.Clear();
        AppendCommand($"YOU > {command}");
        SaveInstruction(command, "DOCK_SYNC_REQUESTED");
        await SyncLumiToWorkstationAsync(userRequested: true);
    }

    private async Task SyncLumiToWorkstationAsync(bool userRequested)
    {
        if (!await _dockSyncGate.WaitAsync(0))
        {
            if (userRequested) AppendCommand("LUMI > A phone sync is already running.");
            return;
        }

        try
        {
            StatusText.Text = "Docking Lumi...";
            TopConnectionText.Text = "PHONE: DOCKING";
            PhoneStateText.Text = "ADB CHECK · DOCKING";

            await RefreshPhoneAsync();
            if (_deviceSerial is null)
            {
                TopConnectionText.Text = "PHONE: NOT DOCKED";
                PhoneStateText.Text = "NOT DOCKED";
                if (userRequested) AppendCommand("LUMI > Dock failed: no authorized Android device is available.");
                return;
            }

            var serial = _deviceSerial;
            var packageDump = await RunAdbAsync($"-s {serial} shell dumpsys package com.distressedelk.lumi", false);
            var versionName = System.Text.RegularExpressions.Regex.Match(packageDump.Output, @"versionName=([^\r\n]+)").Groups[1].Value.Trim();
            var versionCode = System.Text.RegularExpressions.Regex.Match(packageDump.Output, @"versionCode=(\d+)").Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(versionName) && string.IsNullOrWhiteSpace(versionCode))
            {
                TopConnectionText.Text = "PHONE: ADB ONLY";
                PhoneStateText.Text = "ADB ONLY · LUMI NOT VERIFIED";
                StatusText.Text = "Phone visible, Lumi package not verified";
                return;
            }

            PhoneStateText.Text = "ADB CONNECTED · TRANSFERRING";
            TopConnectionText.Text = "PHONE: SYNCING";
            StatusText.Text = "Transferring Lumi files to workstation...";

            var model = (await RunAdbAsync($"-s {serial} shell getprop ro.product.model", false)).Output.Trim();
            var android = (await RunAdbAsync($"-s {serial} shell getprop ro.build.version.release", false)).Output.Trim();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var phoneRoot = Path.Combine(WorkstationRoot, "DockedPhone", SafeSegment(serial));
            var current = Path.Combine(phoneRoot, "Current");
            var staging = Path.Combine(phoneRoot, ".incoming-" + stamp);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(Path.Combine(staging, "Installed"));
            Directory.CreateDirectory(Path.Combine(staging, "Exports"));
            Directory.CreateDirectory(Path.Combine(staging, "Diagnostics"));

            await File.WriteAllTextAsync(Path.Combine(staging, "Diagnostics", "lumi-package.txt"), packageDump.Output + packageDump.Error);
            var props = await RunAdbAsync($"-s {serial} shell getprop", false);
            await File.WriteAllTextAsync(Path.Combine(staging, "Diagnostics", "device-properties.txt"), props.Output + props.Error);
            var battery = await RunAdbAsync($"-s {serial} shell dumpsys battery", false);
            await File.WriteAllTextAsync(Path.Combine(staging, "Diagnostics", "battery.txt"), battery.Output + battery.Error);
            var process = await RunAdbAsync($"-s {serial} shell pidof com.distressedelk.lumi", false);
            await File.WriteAllTextAsync(Path.Combine(staging, "Diagnostics", "lumi-process.txt"), process.Output + process.Error);
            var logcat = await RunAdbAsync($"-s {serial} logcat -d -t 1200", false);
            await File.WriteAllTextAsync(Path.Combine(staging, "Diagnostics", "logcat-recent.txt"), logcat.Output + logcat.Error);

            var pulled = new List<string>();
            var packagePaths = await RunAdbAsync($"-s {serial} shell pm path com.distressedelk.lumi", false);
            foreach (var line in SplitLines(packagePaths.Output).Where(x => x.StartsWith("package:", StringComparison.OrdinalIgnoreCase)))
            {
                var remote = line["package:".Length..].Trim();
                var local = Path.Combine(staging, "Installed", SafeSegment(Path.GetFileName(remote)));
                var result = await RunAdbAsync($"-s {serial} pull \"{remote}\" \"{local}\"", false);
                if (result.ExitCode == 0 && File.Exists(local))
                    pulled.Add(remote);
            }

            var roots = new[]
            {
                (Path: "/sdcard/Android/data/com.distressedelk.lumi/files", PullAll: true, MaxDepth: 7),
                (Path: "/sdcard/Download", PullAll: false, MaxDepth: 4),
                (Path: "/sdcard/Documents", PullAll: false, MaxDepth: 4)
            };

            foreach (var root in roots)
            {
                var listing = await RunAdbAsync($"-s {serial} shell find {root.Path} -maxdepth {root.MaxDepth} -type f", false);
                if (listing.ExitCode != 0) continue;

                foreach (var remote in SplitLines(listing.Output)
                    .Where(x => x.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase))
                    .Where(x => root.PullAll || LooksLikeLumiArtifact(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(250))
                {
                    var local = Path.Combine(staging, "Exports", LocalNameForRemote(remote));
                    var result = await RunAdbAsync($"-s {serial} pull \"{remote}\" \"{local}\"", false);
                    if (result.ExitCode == 0 && File.Exists(local))
                        pulled.Add(remote);
                }
            }

            if (pulled.Count == 0)
            {
                TryDeleteDirectory(staging);
                TopConnectionText.Text = "PHONE: ADB ONLY";
                PhoneStateText.Text = "ADB ONLY · FILE TRANSFER FAILED";
                StatusText.Text = "ADB connected, Lumi transfer failed";
                AppendCommand("LUMI > Phone is visible through ADB, but no Lumi file could be transferred. Dock status remains FAIL.");
                return;
            }

            var manifest = new
            {
                format = "LumiWorkstationDockManifest",
                formatVersion = 1,
                syncedAt = DateTimeOffset.Now,
                serial,
                model,
                android,
                lumi = new { versionCode, versionName },
                transferredPhoneFiles = pulled.Count,
                remoteFiles = pulled,
                note = "Installed APK and phone-accessible Lumi artifacts are synchronized. Android app source code is not stored in the installed APK as a buildable Git working tree; workstation source is commissioned separately from the canonical repository."
            };
            await File.WriteAllTextAsync(
                Path.Combine(staging, "dock-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            Directory.CreateDirectory(phoneRoot);
            TryDeleteDirectory(current);
            Directory.Move(staging, current);
            _lastDockFolder = current;
            await File.WriteAllTextAsync(Path.Combine(phoneRoot, "last-sync.txt"), $"{DateTimeOffset.Now:O}{Environment.NewLine}{current}");

            ReleasePhoneText.Text = string.IsNullOrWhiteSpace(versionCode) ? versionName : $"Code {versionCode}";
            ReleasePhoneText.ToolTip = versionName;
            PhoneStateText.Text = "DOCKED · FILES SYNCED";
            PhoneDetailText.Text = $"Device: {model}{Environment.NewLine}Android: {android}{Environment.NewLine}ADB: Authorized{Environment.NewLine}Serial: {serial}{Environment.NewLine}Lumi: Code {versionCode} · {versionName}{Environment.NewLine}Phone files synced: {pulled.Count}";
            TopConnectionText.Text = "PHONE: DOCKED";
            StatusText.Text = $"Dock PASS · {pulled.Count} phone file(s) synced";
            ApplySourceTruth(pulled.Count);
            AppendCommand($"LUMI > DOCK PASS. Transferred {pulled.Count} phone file(s) to {current}");
            AppendDiagnostic($"Dock PASS: serial={serial}, LumiCode={versionCode}, files={pulled.Count}, folder={current}");
        }
        catch (Exception ex)
        {
            TopConnectionText.Text = "PHONE: SYNC ERROR";
            PhoneStateText.Text = "ADB CONNECTED · SYNC ERROR";
            StatusText.Text = "Dock sync failed";
            AppendCommand("LUMI > Dock sync failed: " + ex.Message);
            AppendDiagnostic("Dock sync failed: " + ex);
        }
        finally
        {
            _dockSyncGate.Release();
        }
    }

    private void OpenDockFolder()
    {
        var folder = _lastDockFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(WorkstationRoot, "DockedPhone");
            Directory.CreateDirectory(folder);
        }
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0);

    private static bool LooksLikeLumiArtifact(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("lumi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("blackbox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("black-box", StringComparison.OrdinalIgnoreCase)
            || name.Contains("distressedelk", StringComparison.OrdinalIgnoreCase)
            || name.Contains("backup", StringComparison.OrdinalIgnoreCase) && path.Contains("lumi", StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalNameForRemote(string remote)
    {
        var leaf = SafeSegment(Path.GetFileName(remote));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote)))[..8].ToLowerInvariant();
        if (leaf.Length > 120) leaf = leaf[..120];
        return $"{hash}-{leaf}";
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    private static string WorkstationVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is null) return "0.4.0";
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string ButtonLabel(Button button)
    {
        if (button.Content is string s) return s;
        if (button.Content is TextBlock t) return t.Text;
        if (button.Content is DependencyObject d)
            return VisualDescendants<TextBlock>(d).FirstOrDefault()?.Text ?? string.Empty;
        return button.Content?.ToString() ?? string.Empty;
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }
}
