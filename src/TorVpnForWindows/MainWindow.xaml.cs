using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TorVpnForWindows.Config;
using TorVpnForWindows.Core;
using TorVpnForWindows.Localization;
using TorVpnForWindows.Ui;

namespace TorVpnForWindows;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly VpnService _vpn;
    private readonly ObservableCollection<LogEntry> _logEntries = [];
    private readonly TrayIcon _tray;

    /// <summary>Suppresses change handlers while controls are being filled in from settings.</summary>
    private bool _loading;

    private bool _reallyClosing;

    public MainWindow(AppSettings settings, VpnService vpn)
    {
        _settings = settings;
        _vpn = vpn;

        InitializeComponent();

        LogList.ItemsSource = _logEntries;

        foreach (var entry in Log.Snapshot())
        {
            _logEntries.Add(entry);
        }

        Log.Entry += OnLogEntry;
        LocManager.LanguageChanged += ApplyStrings;
        _vpn.StatusChanged += OnStatusChanged;
        _vpn.TrafficChanged += OnTrafficChanged;

        _tray = new TrayIcon();
        _tray.ShowRequested += ShowFromTray;
        _tray.ConnectRequested += () => _ = ConnectAsync();
        _tray.DisconnectRequested += () => _ = _vpn.DisconnectAsync();
        _tray.ExitRequested += () => { _reallyClosing = true; Close(); };

        LoadSettingsIntoControls();
        ApplyStrings();
        Render(_vpn.CurrentStatus);

        SourceInitialized += (_, _) => WindowChromeHelper.UseDarkTitleBar(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.StartMinimized)
        {
            if (_settings.MinimizeToTray)
            {
                Hide();
            }
            else
            {
                WindowState = WindowState.Minimized;
            }
        }

        if (_settings.AutoConnect)
        {
            await ConnectAsync();
        }
    }

    // ---------------------------------------------------------------- strings

    private void ApplyStrings()
    {
        Title = Strings.AppTitle;
        HeaderTitle.Text = Strings.AppTitle;
        HeaderSubtitle.Text = Strings.AppSubtitle;

        TabStatus.Header = Strings.TabStatus;
        TabSettings.Header = Strings.TabSettings;
        TabLog.Header = Strings.TabLog;

        NewIdentityButton.Content = Strings.ButtonNewIdentity;
        UdpNotice.Text = Strings.StatusUdpNotice;

        ExitAddressLabel.Text = Strings.StatusExitAddress;
        ExitCountryLabel.Text = Strings.StatusExitCountry;
        DownloadLabel.Text = Strings.StatusDownload;
        UploadLabel.Text = Strings.StatusUpload;

        SectionConnection.Text = Strings.SectionConnection;
        SectionNetwork.Text = Strings.SectionNetwork;
        SectionApplication.Text = Strings.SectionApplication;
        SectionAdvanced.Text = Strings.SectionAdvanced;

        LanguageLabel.Text = Strings.SettingLanguage;
        ExitCountryLabelSetting.Text = Strings.SettingExitCountry;
        ExitCountryHint.Text = Strings.SettingExitCountryHint;
        BridgeLabel.Text = Strings.SettingBridges;
        BridgeHint.Text = Strings.SettingBridgesHint;
        CustomBridgeHint.Text = Strings.SettingBridgesCustomHint;

        KillSwitchLabel.Text = Strings.SettingKillSwitch;
        KillSwitchHint.Text = Strings.SettingKillSwitchHint;
        StrictRouteLabel.Text = Strings.SettingStrictRoute;
        StrictRouteHint.Text = Strings.SettingStrictRouteHint;
        AllowLanLabel.Text = Strings.SettingAllowLan;
        AllowLanHint.Text = Strings.SettingAllowLanHint;

        ExclusionsLabel.Text = Strings.SettingExclusions;
        ExclusionsHint.Text = Strings.SettingExclusionsHint;
        OpenExclusionsButton.Content = Strings.ExclusionsOpen;

        AutoConnectLabel.Text = Strings.SettingAutoConnect;
        MinimizeToTrayLabel.Text = Strings.SettingMinimizeToTray;
        StartMinimizedLabel.Text = Strings.SettingStartMinimized;

        TunNameLabel.Text = Strings.SettingTunName;
        MtuLabel.Text = Strings.SettingMtu;
        RestartNeededText.Text = Strings.SettingRestartNeeded;

        LogCopyButton.Content = Strings.LogCopy;
        LogClearButton.Content = Strings.LogClear;
        LogFolderButton.Content = Strings.LogOpenFolder;
        AutoScrollLabel.Text = Strings.LogAutoScroll;

        RefreshExclusionCount();
        RebuildLocalizedCombos();
        Render(_vpn.CurrentStatus);
        _tray.ApplyStrings();
    }

    private void RebuildLocalizedCombos()
    {
        var wasLoading = _loading;
        _loading = true;

        try
        {
            // Language picker: "follow Windows" plus every lang.<code>.xml that exists.
            var languages = new List<ComboItem>
            {
                new(LocManager.SystemLanguage, Strings.LanguageSystem)
            };

            foreach (var (code, name) in LocManager.Available)
            {
                languages.Add(new ComboItem(code, name));
            }

            LanguageCombo.ItemsSource = languages;
            LanguageCombo.DisplayMemberPath = nameof(ComboItem.Label);
            LanguageCombo.SelectedItem =
                languages.FirstOrDefault(l => string.Equals(l.Value, _settings.Language, StringComparison.OrdinalIgnoreCase))
                ?? languages[0];

            var countries = ExitCountries.Build(Strings.ExitCountryAny)
                .Select(c => new ComboItem(c.Code ?? string.Empty, c.DisplayName))
                .ToList();

            ExitCountryCombo.ItemsSource = countries;
            ExitCountryCombo.DisplayMemberPath = nameof(ComboItem.Label);
            ExitCountryCombo.SelectedItem =
                countries.FirstOrDefault(c => string.Equals(c.Value, _settings.ExitCountry ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? countries[0];

            var bridgeModes = new List<ComboItem>
            {
                new(nameof(BridgeMode.None), Strings.BridgeNone),
                new(nameof(BridgeMode.Obfs4), Strings.BridgeObfs4),
                new(nameof(BridgeMode.Snowflake), Strings.BridgeSnowflake),
                new(nameof(BridgeMode.Meek), Strings.BridgeMeek),
                new(nameof(BridgeMode.Custom), Strings.BridgeCustom)
            };

            BridgeCombo.ItemsSource = bridgeModes;
            BridgeCombo.DisplayMemberPath = nameof(ComboItem.Label);
            BridgeCombo.SelectedItem =
                bridgeModes.FirstOrDefault(b => b.Value == _settings.BridgeMode.ToString())
                ?? bridgeModes[0];
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void LoadSettingsIntoControls()
    {
        _loading = true;

        try
        {
            KillSwitchToggle.IsChecked = _settings.KillSwitch;
            StrictRouteToggle.IsChecked = _settings.StrictRoute;
            AllowLanToggle.IsChecked = _settings.AllowLan;
            AutoConnectToggle.IsChecked = _settings.AutoConnect;
            MinimizeToTrayToggle.IsChecked = _settings.MinimizeToTray;
            StartMinimizedToggle.IsChecked = _settings.StartMinimized;

            TunNameBox.Text = _settings.TunInterfaceName;
            MtuBox.Text = _settings.Mtu.ToString(CultureInfo.InvariantCulture);
            CustomBridgeBox.Text = string.Join(Environment.NewLine, _settings.CustomBridges);

            CustomBridgePanel.Visibility =
                _settings.BridgeMode == BridgeMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshExclusionCount()
    {
        try
        {
            var count = ExclusionList.Read().Count;
            ExclusionsCount.Text = count == 0
                ? Strings.ExclusionsNone
                : string.Format(CultureInfo.CurrentCulture, Strings.ExclusionsCountFormat, count);
        }
        catch (Exception ex)
        {
            Log.Error("Could not count the excluded applications", ex);
            ExclusionsCount.Text = Strings.ExclusionsNone;
        }
    }

    // ---------------------------------------------------------------- rendering

    private void OnStatusChanged(VpnStatus status) => Dispatcher.Invoke(() => Render(status));

    private void OnTrafficChanged(long read, long written) => Dispatcher.Invoke(() =>
    {
        DownloadValue.Text = FormatBytes(read);
        UploadValue.Text = FormatBytes(written);
    });

    private void Render(VpnStatus status)
    {
        var connected = status.State == VpnState.Connected;
        var busy = status.State is VpnState.Preparing or VpnState.Bootstrapping
            or VpnState.EstablishingTunnel or VpnState.Disconnecting;

        PowerToggle.IsChecked = connected || status.State == VpnState.Interrupted;
        PowerToggle.IsEnabled = !busy;

        StateText.Text = status.State switch
        {
            VpnState.Disconnected => Strings.StateDisconnected,
            VpnState.Preparing => Strings.StatePreparing,
            VpnState.Bootstrapping => Strings.StateBootstrapping,
            VpnState.EstablishingTunnel => Strings.StateEstablishingTunnel,
            VpnState.Connected => Strings.StateConnected,
            VpnState.Disconnecting => Strings.StateDisconnecting,
            VpnState.Interrupted => Strings.StateInterrupted,
            VpnState.Failed => Strings.StateFailed,
            _ => status.State.ToString()
        };

        // While connected the message carries the result of the tunnel verification, which is
        // where an outside product blocking the traffic gets reported.
        StateHint.Text = status.State switch
        {
            VpnState.Connected => status.Message ?? Strings.HintConnected,
            VpnState.Interrupted => Strings.HintInterrupted,
            VpnState.Failed => status.Message ?? Strings.HintDisconnected,
            _ => Strings.HintDisconnected
        };

        var connectedButBlocked = status.State == VpnState.Connected && status.Message is not null;

        StateHint.Foreground = connectedButBlocked
            ? (System.Windows.Media.Brush)FindResource("WarningBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");

        StateText.Foreground = status.State switch
        {
            VpnState.Connected when connectedButBlocked => (System.Windows.Media.Brush)FindResource("WarningBrush"),
            VpnState.Connected => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            VpnState.Failed => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            VpnState.Interrupted => (System.Windows.Media.Brush)FindResource("WarningBrush"),
            _ => (System.Windows.Media.Brush)FindResource("TextBrush")
        };

        var showProgress = status.State is VpnState.Bootstrapping or VpnState.EstablishingTunnel;
        ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        BootstrapBar.Value = status.BootstrapProgress;
        BootstrapText.Text = showProgress
            ? $"{status.BootstrapProgress}%  {status.BootstrapSummary}".TrimEnd()
            : string.Empty;

        var showSession = connected || status.State == VpnState.Interrupted;
        ExitCard.Visibility = showSession ? Visibility.Visible : Visibility.Collapsed;
        TrafficPanel.Visibility = showSession ? Visibility.Visible : Visibility.Collapsed;
        UdpNotice.Visibility = showSession ? Visibility.Visible : Visibility.Collapsed;

        if (status.Exit is { } exit)
        {
            ExitAddressValue.Text = exit.IpAddress;
            ExitCountryValue.Text = exit.CountryCode is null
                ? Strings.StatusUnknown
                : $"{ExitCountries.DisplayNameOf(exit.CountryCode)} ({exit.CountryCode.ToUpperInvariant()})";

            ExitConfirmText.Text = exit.ConfirmedTor ? Strings.StatusConfirmed : Strings.StatusNotConfirmed;
            ExitConfirmText.Foreground = exit.ConfirmedTor
                ? (System.Windows.Media.Brush)FindResource("MutedBrush")
                : (System.Windows.Media.Brush)FindResource("WarningBrush");
        }
        else if (showSession)
        {
            ExitAddressValue.Text = Strings.StatusChecking;
            ExitCountryValue.Text = "—";
            ExitConfirmText.Text = string.Empty;
        }

        NewIdentityButton.IsEnabled = connected;
        NewIdentityButton.Content = Strings.ButtonNewIdentity;

        _tray.Update(connected, status.State);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unit]);
    }

    private void OnLogEntry(LogEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        _logEntries.Add(entry);

        while (_logEntries.Count > 2000)
        {
            _logEntries.RemoveAt(0);
        }

        if (AutoScrollToggle.IsChecked == true && _logEntries.Count > 0)
        {
            LogList.ScrollIntoView(_logEntries[^1]);
        }
    });

    // ---------------------------------------------------------------- actions

    private async void OnPowerToggleClick(object sender, RoutedEventArgs e)
    {
        if (_vpn.CanDisconnect && _vpn.State != VpnState.Failed)
        {
            await _vpn.DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _vpn.ConnectAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Connect threw", ex);
            MessageBox.Show(this, ex.Message, Strings.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnNewIdentityClick(object sender, RoutedEventArgs e)
    {
        NewIdentityButton.IsEnabled = false;

        try
        {
            await _vpn.NewIdentityAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Requesting a new circuit threw", ex);
        }
        finally
        {
            NewIdentityButton.IsEnabled = _vpn.State == VpnState.Connected;
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboItem item)
        {
            return;
        }

        _settings.Language = item.Value;
        _settings.Save();
        LocManager.Apply(item.Value);
    }

    private void OnExitCountryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ExitCountryCombo.SelectedItem is not ComboItem item)
        {
            return;
        }

        _settings.ExitCountry = string.IsNullOrEmpty(item.Value) ? null : item.Value;
        _settings.Save();
    }

    private void OnBridgeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BridgeCombo.SelectedItem is not ComboItem item)
        {
            return;
        }

        if (Enum.TryParse<BridgeMode>(item.Value, out var mode))
        {
            _settings.BridgeMode = mode;
            _settings.Save();
            CustomBridgePanel.Visibility = mode == BridgeMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnCustomBridgesLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CustomBridges = CustomBridgeBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        _settings.Save();
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.KillSwitch = KillSwitchToggle.IsChecked == true;
        _settings.StrictRoute = StrictRouteToggle.IsChecked == true;
        _settings.AllowLan = AllowLanToggle.IsChecked == true;
        _settings.AutoConnect = AutoConnectToggle.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        _settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnAdvancedLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var name = TunNameBox.Text.Trim();
        _settings.TunInterfaceName = name.Length > 0 ? name : "TorVPN";
        TunNameBox.Text = _settings.TunInterfaceName;

        if (int.TryParse(MtuBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu)
            && mtu is >= 576 and <= 9000)
        {
            _settings.Mtu = mtu;
        }

        MtuBox.Text = _settings.Mtu.ToString(CultureInfo.InvariantCulture);
        _settings.Save();
    }

    private void OnOpenExclusionsClick(object sender, RoutedEventArgs e)
    {
        ExclusionList.Open();
        RefreshExclusionCount();
    }

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _logEntries.Select(entry => entry.Display)));
        }
        catch (Exception ex)
        {
            Log.Error("Copying the log failed", ex);
        }
    }

    private void OnLogClearClick(object sender, RoutedEventArgs e) => _logEntries.Clear();

    private void OnLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.LogDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the log folder failed", ex);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------- lifetime

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (!_reallyClosing && _vpn.State is VpnState.Connected or VpnState.Interrupted)
        {
            var answer = MessageBox.Show(
                this,
                Strings.ConfirmQuitWhileConnected,
                Strings.ConfirmQuitTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        Log.Entry -= OnLogEntry;
        LocManager.LanguageChanged -= ApplyStrings;
        _vpn.StatusChanged -= OnStatusChanged;
        _vpn.TrafficChanged -= OnTrafficChanged;
        _tray.Dispose();

        base.OnClosing(e);
        Application.Current.Shutdown();
    }

    private sealed record ComboItem(string Value, string Label);
}
