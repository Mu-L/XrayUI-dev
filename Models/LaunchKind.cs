namespace XrayUI.Models
{
    /// <summary>How this process was started, as far as auto-connect cares.</summary>
    public enum LaunchKind
    {
        /// <summary>Jump-list, --tun or --parent-pid relaunch: connects its own target or is mid-handover.</summary>
        Other,
        /// <summary>The autostart task (--startup-minimized). Follows AppSettings.IsAutoConnect.</summary>
        Boot,
        /// <summary>Opened by hand. Follows AppSettings.IsAutoConnectOnOpen.</summary>
        ManualOpen,
    }
}
