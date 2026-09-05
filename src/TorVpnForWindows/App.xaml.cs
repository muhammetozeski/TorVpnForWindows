using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using TorVpnForWindows.Config;
using TorVpnForWindows.Core;
using TorVpnForWindows.Localization;

namespace TorVpnForWindows;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private VpnService? _vpn;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppPaths.EnsureDirectories();

        var settings = AppSettings.Load();

        // On a first run the language follows the Windows display language, and the resolved value
        // is written back so later changes to Windows do not move it under the user.
        LocManager.Init(settings.Language);

        if (settings.IsFirstRun)
        {
            settings.Language = LocManager.Current;
            settings.Save();
        }

        Log.App($"Tor VPN for Windows {AppPaths.PayloadVersion} starting, language {LocManager.Current}");

        if (!IsElevated())
        {
            MessageBox.Show(Strings.ErrorNeedsAdmin, Strings.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!ClaimSingleInstance())
        {
            MessageBox.Show(Strings.ErrorAlreadyRunning, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        ExclusionList.EnsureExists();

        _vpn = new VpnService(settings);

        var window = new MainWindow(settings, _vpn);
        MainWindow = window;
        window.Show();
    }

    private bool ClaimSingleInstance()
    {
        try
        {
            // Global so a second elevated instance in another session is caught as well; two
            // instances would fight over the TUN adapter and the default route.
            _singleInstance = new Mutex(initiallyOwned: true, @"Global\TorVpnForWindows.SingleInstance", out var created);

            if (!created)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
            }

            return created;
        }
        catch (Exception ex)
        {
            Log.Error("Could not claim the single instance mutex", ex);
            return true; // Better to start than to refuse over a mutex failure.
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Error("Could not determine the privilege level", ex);
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Tearing the session down here is what removes the routes and the adapter.
            _vpn?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Log.Error("Shutting the session down failed", ex);
        }

        try
        {
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Releasing the single instance mutex failed", ex);
        }

        Log.App("Tor VPN for Windows stopped");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread", e.Exception);

        MessageBox.Show(
            e.Exception.Message,
            Strings.ErrorTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // The window stays usable: a failed action should not take the whole tunnel down with it.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Error("Unhandled exception", exception);
        }
        else
        {
            Log.App($"Unhandled non-exception error: {e.ExceptionObject}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
