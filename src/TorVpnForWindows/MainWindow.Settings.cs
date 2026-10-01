using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TorVpnForWindows.Config;
using TorVpnForWindows.Localization;

namespace TorVpnForWindows;

public partial class MainWindow
{
    /// <summary>Rebuilds the profile picker and protects every editor while Default is selected.</summary>
    void RefreshProfileControls()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var profiles = new List<ComboItem> { new(AppSettings.DefaultProfileId, "Default") };
            profiles.AddRange(_settings.Profiles.Select(profile => new ComboItem(profile.Id, profile.Name)));
            ProfileCombo.ItemsSource = profiles;
            ProfileCombo.DisplayMemberPath = nameof(ComboItem.Label);
            ProfileCombo.SelectedItem = profiles.FirstOrDefault(profile => profile.Value == _settings.ActiveProfileId);
            ProfileNameBox.Text = _settings.IsDefaultProfile ? "Default" :
                _settings.Profiles.FirstOrDefault(profile => profile.Id == _settings.ActiveProfileId)?.Name ?? string.Empty;
            ProfileNameBox.IsEnabled = !_settings.IsDefaultProfile;
            SettingsEditor.IsEnabled = !_settings.IsDefaultProfile;
            ProfileNameBox.ToolTip = Strings.ProfileName;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>Applies profile texts using the existing language change notification.</summary>
    void ApplyProfileStrings()
    {
        ProfileLabel.Text = Strings.SettingProfile;
        ProfileNameLabel.Text = Strings.ProfileName;
        NewProfileButton.ToolTip = Strings.ProfileNew;
        System.Windows.Automation.AutomationProperties.SetName(NewProfileButton, Strings.ProfileNew);
        Application.Current.Resources["SettingResetText"] = Strings.SettingReset;
        RefreshProfileControls();
    }

    /// <summary>Switches the complete profile and refreshes the running connection when needed.</summary>
    async void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not ComboItem item || item.Value == _settings.ActiveProfileId)
        {
            return;
        }

        if (_settings.SelectProfile(item.Value))
        {
            ReloadProfileSettings();
            await Task.Run(() => _vpn.ApplySettingsChanges("The settings profile changed."));
        }
    }

    /// <summary>Creates an editable copy of Default and selects its name for editing.</summary>
    async void OnNewProfileClick(object sender, RoutedEventArgs e)
    {
        _settings.CreateProfile();
        ReloadProfileSettings();
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
        await Task.Run(() => _vpn.ApplySettingsChanges("A new settings profile was selected."));
    }

    /// <summary>Rebinds mutable lists and controls after changing or resetting profile values.</summary>
    void ReloadProfileSettings()
    {
        LoadSettingsIntoControls();
        LocManager.Apply(_settings.Language);
    }

    /// <summary>Saves the edited name when focus leaves the name field.</summary>
    void OnProfileNameLostFocus(object sender, RoutedEventArgs e) => SaveProfileName();

    /// <summary>Accepts a profile name with Enter without requiring a separate save button.</summary>
    void OnProfileNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveProfileName();
            e.Handled = true;
        }
    }

    /// <summary>Persists a valid name or keeps the existing name and explains the rejected value.</summary>
    void SaveProfileName()
    {
        if (_loading || _settings.IsDefaultProfile)
        {
            return;
        }

        var active = _settings.Profiles.First(profile => profile.Id == _settings.ActiveProfileId);
        if (ProfileNameBox.Text.Trim() == active.Name)
        {
            return;
        }

        if (!_settings.RenameActiveProfile(ProfileNameBox.Text))
        {
            ProfileNameBox.ToolTip = Strings.ProfileNameInvalid;
            ProfileNameBox.Text = active.Name;
            return;
        }

        RefreshProfileControls();
    }

    /// <summary>Resets the single setting identified by the clicked button, then updates its controls.</summary>
    async void OnResetSettingClick(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: string setting } || !_settings.ResetSetting(setting))
        {
            return;
        }

        ReloadProfileSettings();
        if (setting is nameof(AppSettings.ExitCountry) or nameof(AppSettings.BridgeMode) or
            nameof(AppSettings.MeekFront) or nameof(AppSettings.KillSwitch) or nameof(AppSettings.StrictRoute) or
            nameof(AppSettings.AllowLan) or nameof(AppSettings.TunInterfaceName) or nameof(AppSettings.Mtu))
        {
            await Task.Run(() => _vpn.ApplySettingsChanges($"The setting {setting} was reset."));
        }
    }
}
