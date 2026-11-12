using System;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// Guesses the kind of device from what the server knows about the session:
/// the app name and device name the app registered with, and the HTTP User-Agent.
/// </summary>
public static partial class DeviceDetector
{
    /// <summary>
    /// Detects the device kind.
    /// </summary>
    /// <param name="client">App name, e.g. "Jellyfin Android".</param>
    /// <param name="deviceName">Device name, e.g. "Pixel 8" or "iPad".</param>
    /// <param name="userAgent">HTTP User-Agent, may be empty.</param>
    /// <returns>The detected kind.</returns>
    public static DeviceKind Detect(string? client, string? deviceName, string? userAgent)
    {
        client ??= string.Empty;
        deviceName ??= string.Empty;
        userAgent ??= string.Empty;
        var all = client + " | " + deviceName + " | " + userAgent;

        if (TvPattern().IsMatch(all))
        {
            return DeviceKind.Tv;
        }

        if (TabletPattern().IsMatch(client + " | " + deviceName))
        {
            return DeviceKind.Tablet;
        }

        // Android browsers put "Mobile" in the UA on phones but not on tablets.
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("DownloadManager", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("okhttp", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("ExoPlayer", StringComparison.OrdinalIgnoreCase)
            && userAgent.Contains("Mozilla", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.Tablet;
        }

        if (PhonePattern().IsMatch(all))
        {
            return DeviceKind.Phone;
        }

        if (DesktopPattern().IsMatch(all))
        {
            return DeviceKind.Desktop;
        }

        return DeviceKind.Other;
    }

    /// <summary>
    /// Picks the profile for a device. Order: explicit assignment, name match, detected kind, the "Other" kind.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="deviceId">Jellyfin device id.</param>
    /// <param name="client">App name.</param>
    /// <param name="deviceName">Device name.</param>
    /// <param name="userAgent">User-Agent.</param>
    /// <returns>The profile, or null when nothing matches.</returns>
    public static TranscodeProfile? SelectProfile(PluginConfiguration config, string? deviceId, string? client, string? deviceName, string? userAgent)
    {
        var profiles = config.Profiles ?? [];

        if (!string.IsNullOrEmpty(deviceId))
        {
            var assigned = config.DeviceAssignments?.FirstOrDefault(a => string.Equals(a.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            var byAssignment = assigned is null ? null : FindByName(config, assigned.Profile);
            if (byAssignment is not null)
            {
                return byAssignment;
            }
        }

        foreach (var profile in profiles)
        {
            var names = (profile.MatchNames ?? string.Empty)
                .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Any(n => Contains(client, n) || Contains(deviceName, n)))
            {
                return profile;
            }
        }

        var kind = Detect(client, deviceName, userAgent);
        return profiles.FirstOrDefault(p => p.DeviceKind == kind)
            ?? profiles.FirstOrDefault(p => p.DeviceKind == DeviceKind.Other);
    }

    /// <summary>
    /// Finds a profile by name.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="name">Profile name.</param>
    /// <returns>The profile or null.</returns>
    public static TranscodeProfile? FindByName(PluginConfiguration config, string? name)
        => (config.Profiles ?? []).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bTV\b|Android ?TV|Google ?TV|tvOS|AppleTV|Apple TV|webOS|Tizen|Roku|BRAVIA|SHIELD|Chromecast|\bAFT[A-Z0-9]*|Fire ?TV|Xbox|PlayStation|Kodi|\bCrKey\b|SmartTV|HbbTV|Infuse.*tvOS", RegexOptions.IgnoreCase)]
    private static partial Regex TvPattern();

    [GeneratedRegex(@"iPad|Tablet|\bTab\b|Galaxy Tab|\bSM-[TPX]\d|\bKF[A-Z]{2,}|Lenovo ?TB|\bTB-?\d|MatePad|MediaPad|Xiaomi Pad|Redmi Pad|OnePlus Pad|Nexus (7|9|10)\b|Pixel (C|Tablet)\b|\bPad\b", RegexOptions.IgnoreCase)]
    private static partial Regex TabletPattern();

    [GeneratedRegex(@"Android|iPhone|iPod|\biOS\b|Swiftfin|Findroid|Streamyfin|Finamp|Mobile|Pixel|Galaxy|\bSM-[ANGFSEM]\d|OnePlus|Xiaomi|Redmi|Motorola|moto |Nokia|Sony Xperia|Huawei|OPPO|vivo|realme", RegexOptions.IgnoreCase)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"Jellyfin Web|Jellyfin Media Player|Jellyfin Desktop|Windows|Macintosh|Mac OS|Linux x86_64|X11|Chrome|Firefox|Edge|Safari|Opera|Vivaldi|Brave|\bmpv\b|Jellyfin MPV Shim|Feishin", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopPattern();
}
