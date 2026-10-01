namespace TorVpnForWindows.Core;

/// <summary>Shared application identity used for folders, display text and the instance mutex.</summary>
public static class AppConstants
{
    public const string Id = "TorVpnForWindows";
    public const string DisplayName = "Tor VPN for Windows";
    public const string InstanceMutex = @"Global\" + Id + ".SingleInstance";
}
