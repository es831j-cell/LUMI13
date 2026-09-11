using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lumi.DockingStation;

public partial class MainWindow
{
    private static readonly bool PhoneDetectionUpgradeHook = RegisterPhoneDetectionUpgradeHook();
    private DispatcherTimer? _phonePresenceTimer;
    private int _phoneRecoveryBusy;

    private static bool RegisterPhoneDetectionUpgradeHook()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(PhoneDetectionUpgrade_Loaded), true);
        return true;
    }

    private static void PhoneDetectionUpgrade_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.SelectBundledAdbFirst();
        window.StartPhonePresenceWatch();

        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(2200);
            if (window._deviceSerial is null)
                await window.RecoverPhoneDetectionAsync("startup");
        }));
    }

    private void SelectBundledAdbFirst()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (!File.Exists(bundled)) return;

        if (!string.Equals(_adbPath, bundled, StringComparison.OrdinalIgnoreCase))
        {
            _adbPath = bundled;
            AppendDiagnostic("ADB source locked to bundled Workstation copy: " + bundled);
        }
    }

    private void StartPhonePresenceWatch()
    {
        if (_phonePresenceTimer is not null) return;
        _phonePresenceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _phonePresenceTimer.Tick += async (_, _) =>
        {
            if (_deviceSerial is not null) return;
            await RecoverPhoneDetectionAsync("presence-watch", quiet: true);
        };
        _phonePresenceTimer.Start();
    }

    private async Task RecoverPhoneDetectionAsync(string reason, bool quiet = false)
    {
        if (Interlocked.Exchange(ref _phoneRecoveryBusy, 1) != 0) return;
        try
        {
            SelectBundledAdbFirst();
            if (_adbPath is null)
            {
                await RefreshPhoneAsync();
                return;
            }

            await RunAdbAsync("start-server", false);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await RefreshPhoneAsync();
                if (_deviceSerial is not null) return;

                if (PhoneStateText.Text.Contains("NOT AUTHORIZED", StringComparison.OrdinalIgnoreCase)
                    || PhoneStateText.Text.Contains("AUTHORIZE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!quiet)
                        AppendCommand("LUMI > Phone is physically visible but USB debugging authorization is waiting on the phone. Unlock it and approve the RSA prompt.");
                    return;
                }

                if (attempt < 3) await Task.Delay(700 * attempt);
            }

            // A stale ADB server can survive a Workstation update. Restart it once, then re-enumerate.
            AppendDiagnostic($"Phone detection recovery ({reason}): no authorized device after retries; restarting bundled ADB server once.");
            await RunAdbAsync("kill-server", false);
            await Task.Delay(500);
            await RunAdbAsync("start-server", false);
            await Task.Delay(900);
            await RefreshPhoneAsync();

            if (_deviceSerial is not null)
            {
                AppendCommand("LUMI > PHONE RECOVERY PASS. Reconnected through the Workstation's bundled ADB runtime.");
                return;
            }

            if (!quiet)
                AppendCommand("LUMI > PHONE RECOVERY BLOCKED. USB is connected, but ADB still reports no authorized device. Keep the phone unlocked and check for an 'Allow USB debugging' prompt, then press Refresh.");
        }
        catch (Exception ex)
        {
            AppendDiagnostic("Phone detection recovery failed: " + ex.Message);
            if (!quiet) AppendCommand("LUMI > Phone detection recovery failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _phoneRecoveryBusy, 0);
        }
    }
}
