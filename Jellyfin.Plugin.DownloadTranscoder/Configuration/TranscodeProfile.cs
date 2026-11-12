namespace Jellyfin.Plugin.DownloadTranscoder.Configuration;

/// <summary>
/// The kind of device a download comes from, as detected by <see cref="DeviceDetector"/>.
/// </summary>
public enum DeviceKind
{
    /// <summary>
    /// Could not tell. Matches profiles with kind <see cref="Other"/>.
    /// </summary>
    Other = 0,

    /// <summary>
    /// A phone.
    /// </summary>
    Phone = 1,

    /// <summary>
    /// A tablet.
    /// </summary>
    Tablet = 2,

    /// <summary>
    /// A TV, streaming stick or set-top box.
    /// </summary>
    Tv = 3,

    /// <summary>
    /// A computer: web browser or desktop app.
    /// </summary>
    Desktop = 4
}

/// <summary>
/// Output quality for one group of devices.
/// </summary>
public class TranscodeProfile
{
    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device kind this profile is used for when detection finds it.
    /// </summary>
    public DeviceKind DeviceKind { get; set; } = DeviceKind.Other;

    /// <summary>
    /// Gets or sets app or device names (comma separated, case insensitive, partial match) that always use this profile,
    /// e.g. "Findroid, Pixel Tablet". Checked before device kind.
    /// </summary>
    public string MatchNames { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether downloads are transcoded. False sends the original file.
    /// </summary>
    public bool Transcode { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum output height in pixels.
    /// </summary>
    public int MaxHeight { get; set; } = 720;

    /// <summary>
    /// Gets or sets the video bitrate in kbit/s.
    /// </summary>
    public int VideoBitrateKbps { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the ffmpeg video encoder, e.g. libx264, libx265, h264_nvenc, h264_qsv.
    /// </summary>
    public string VideoEncoder { get; set; } = "libx264";

    /// <summary>
    /// Gets or sets the encoder preset (empty to omit).
    /// </summary>
    public string EncoderPreset { get; set; } = "veryfast";

    /// <summary>
    /// Gets or sets the AAC bitrate per audio track in kbit/s.
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 128;

    /// <summary>
    /// Gets or sets the number of audio channels.
    /// </summary>
    public int AudioChannels { get; set; } = 2;

    /// <summary>
    /// Builds the default profiles.
    /// </summary>
    /// <returns>The defaults.</returns>
    public static TranscodeProfile[] Defaults() =>
    [
        new TranscodeProfile
        {
            Name = "Phone", DeviceKind = DeviceKind.Phone,
            MaxHeight = 720, VideoBitrateKbps = 2000, AudioBitrateKbps = 128, AudioChannels = 2
        },
        new TranscodeProfile
        {
            Name = "Tablet", DeviceKind = DeviceKind.Tablet,
            MaxHeight = 1080, VideoBitrateKbps = 4000, AudioBitrateKbps = 160, AudioChannels = 2
        },
        new TranscodeProfile
        {
            Name = "TV", DeviceKind = DeviceKind.Tv, Transcode = false,
            MaxHeight = 1080, VideoBitrateKbps = 8000, VideoEncoder = "libx265", EncoderPreset = "fast",
            AudioBitrateKbps = 384, AudioChannels = 6
        },
        new TranscodeProfile
        {
            Name = "Computer", DeviceKind = DeviceKind.Desktop, Transcode = false,
            MaxHeight = 1080, VideoBitrateKbps = 6000, AudioBitrateKbps = 192
        },
        new TranscodeProfile
        {
            Name = "Other", DeviceKind = DeviceKind.Other,
            MaxHeight = 1080, VideoBitrateKbps = 4000, AudioBitrateKbps = 160, AudioChannels = 2
        }
    ];
}

/// <summary>
/// Pins one registered device to a profile, overriding detection.
/// </summary>
public class DeviceAssignment
{
    /// <summary>
    /// Gets or sets the Jellyfin device id.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    public string Profile { get; set; } = string.Empty;
}
