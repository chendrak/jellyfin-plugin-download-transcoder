using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// The video encoder picked for one transcode, with the ffmpeg arguments it needs.
/// </summary>
/// <param name="Encoder">ffmpeg encoder name, e.g. h264_qsv.</param>
/// <param name="HardwareType">Jellyfin hardware acceleration type (nvenc, qsv, vaapi, …), or null for CPU.</param>
/// <param name="InitArguments">Arguments placed before the input, e.g. hardware device setup.</param>
/// <param name="FilterSuffix">Filters appended after scaling, e.g. uploading frames to the GPU.</param>
/// <param name="FullPipeline">True when decoding, scaling and tone mapping also run on the GPU (VAAPI filters).</param>
public sealed record EncoderChoice(string Encoder, string? HardwareType, IReadOnlyList<string> InitArguments, string FilterSuffix, bool FullPipeline = false)
{
    /// <summary>
    /// Gets a value indicating whether the GPU encodes.
    /// </summary>
    public bool IsHardware => HardwareType is not null;

    /// <summary>
    /// Gets a short description for the activity page, e.g. "GPU (qsv: h264_qsv)".
    /// </summary>
    public string Label => !IsHardware ? "CPU (" + Encoder + ")"
        : FullPipeline ? "GPU, full pipeline (" + HardwareType + ": " + Encoder + ")"
        : "GPU encode only (" + HardwareType + ": " + Encoder + ")";

    /// <summary>
    /// Builds the GPU video filter for a full pipeline: optional HDR tone map, scale, and for QSV a hand-over to the QSV encoder.
    /// </summary>
    /// <param name="maxHeight">Maximum output height.</param>
    /// <param name="hdr">Whether to tone map.</param>
    /// <returns>The filter.</returns>
    public string FullPipelineFilter(int maxHeight, bool hdr)
    {
        var h = maxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var filter = (hdr ? "tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709," : string.Empty)
            + "scale_vaapi=w=-2:h='min(" + h + ",ih)':format=nv12";
        return HardwareType == "qsv" ? filter + ",hwmap=derive_device=qsv,format=qsv" : filter;
    }
}

/// <summary>
/// Maps a profile and Jellyfin's hardware acceleration settings to an ffmpeg encoder.
/// Decoding, scaling and tone mapping stay on the CPU; only encoding, the expensive part, moves to the GPU.
/// </summary>
public static class HardwareEncoder
{
    private const string DefaultRenderNode = "/dev/dri/renderD128";

    private static readonly string[] KnownTypes = ["nvenc", "qsv", "vaapi", "videotoolbox", "amf", "rkmpp", "v4l2m2m"];

    /// <summary>
    /// Lists the encoders to try, best first: full GPU pipeline (Intel/AMD on Linux), GPU encode only, then CPU.
    /// </summary>
    /// <param name="profile">The quality profile.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="jellyfinHardwareType">Jellyfin's HardwareAccelerationType setting.</param>
    /// <param name="jellyfinHardwareEncoding">Jellyfin's "Enable hardware encoding" setting.</param>
    /// <param name="vaapiDevice">Jellyfin's VA-API device.</param>
    /// <param name="qsvDevice">Jellyfin's QSV device.</param>
    /// <returns>The choices, without duplicates.</returns>
    public static IReadOnlyList<EncoderChoice> Chain(
        TranscodeProfile profile,
        PluginConfiguration config,
        string? jellyfinHardwareType,
        bool jellyfinHardwareEncoding,
        string? vaapiDevice,
        string? qsvDevice)
    {
        var list = new List<EncoderChoice>();
        var encodeOnly = Resolve(profile, config, jellyfinHardwareType, jellyfinHardwareEncoding, vaapiDevice, qsvDevice);
        if (encodeOnly.IsHardware && config.FullGpuPipeline && !OperatingSystem.IsWindows()
            && encodeOnly.HardwareType is "vaapi" or "qsv")
        {
            var device = encodeOnly.HardwareType == "qsv" && !string.IsNullOrWhiteSpace(qsvDevice) ? qsvDevice
                : string.IsNullOrWhiteSpace(vaapiDevice) ? DefaultRenderNode : vaapiDevice;
            var init = new List<string>
            {
                "-init_hw_device", "vaapi=va:" + device,
                "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi", "-hwaccel_device", "va"
            };
            if (encodeOnly.HardwareType == "qsv")
            {
                init.AddRange(["-init_hw_device", "qsv=qs@va"]);
            }

            init.AddRange(["-filter_hw_device", "va"]);
            list.Add(encodeOnly with { InitArguments = init, FilterSuffix = string.Empty, FullPipeline = true });
        }

        list.Add(encodeOnly);
        if (encodeOnly.IsHardware)
        {
            list.Add(Resolve(profile, config, jellyfinHardwareType, jellyfinHardwareEncoding, vaapiDevice, qsvDevice, forceSoftware: true));
        }

        return list;
    }

    /// <summary>
    /// Picks the encoder.
    /// </summary>
    /// <param name="profile">The quality profile.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="jellyfinHardwareType">Jellyfin's HardwareAccelerationType setting, e.g. "qsv" or "none".</param>
    /// <param name="jellyfinHardwareEncoding">Jellyfin's "Enable hardware encoding" setting.</param>
    /// <param name="vaapiDevice">Jellyfin's VA-API device.</param>
    /// <param name="qsvDevice">Jellyfin's QSV device.</param>
    /// <param name="forceSoftware">Use the CPU regardless (fallback after a GPU failure).</param>
    /// <returns>The choice.</returns>
    public static EncoderChoice Resolve(
        TranscodeProfile profile,
        PluginConfiguration config,
        string? jellyfinHardwareType,
        bool jellyfinHardwareEncoding,
        string? vaapiDevice,
        string? qsvDevice,
        bool forceSoftware = false)
    {
        var configured = string.IsNullOrWhiteSpace(profile.VideoEncoder) ? "libx264" : profile.VideoEncoder.Trim();
        var hevc = configured.Contains("265", StringComparison.Ordinal) || configured.Contains("hevc", StringComparison.OrdinalIgnoreCase);
        var codec = hevc ? "hevc" : "h264";
        var software = new EncoderChoice(hevc ? "libx265" : "libx264", null, [], string.Empty);

        if (forceSoftware)
        {
            return software;
        }

        string? type;
        string encoder;
        if (!configured.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
        {
            // An explicit hardware encoder such as h264_nvenc; the part after "_" names the type.
            encoder = configured;
            var underscore = configured.LastIndexOf('_');
            type = underscore > 0 ? configured[(underscore + 1)..].ToLowerInvariant() : null;
        }
        else if (config.UseJellyfinHardwareAcceleration && jellyfinHardwareEncoding
            && !string.IsNullOrEmpty(jellyfinHardwareType)
            && Array.IndexOf(KnownTypes, jellyfinHardwareType.ToLowerInvariant()) >= 0)
        {
            type = jellyfinHardwareType.ToLowerInvariant();
            encoder = codec + "_" + type;
        }
        else
        {
            return new EncoderChoice(configured, null, [], string.Empty);
        }

        var vaapi = string.IsNullOrWhiteSpace(vaapiDevice) ? DefaultRenderNode : vaapiDevice;
        return type switch
        {
            "nvenc" => new EncoderChoice(encoder, type, [], ",format=yuv420p"),
            "vaapi" => new EncoderChoice(encoder, type, ["-init_hw_device", "vaapi=va:" + vaapi, "-filter_hw_device", "va"], ",format=nv12,hwupload"),
            "qsv" => OperatingSystem.IsWindows()
                ? new EncoderChoice(encoder, type, ["-init_hw_device", "qsv=qs", "-filter_hw_device", "qs"], ",format=nv12,hwupload=extra_hw_frames=64")
                : new EncoderChoice(
                    encoder,
                    type,
                    ["-init_hw_device", "vaapi=va:" + (string.IsNullOrWhiteSpace(qsvDevice) ? vaapi : qsvDevice), "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs"],
                    ",format=nv12,hwupload=extra_hw_frames=64"),
            "videotoolbox" or "amf" or "rkmpp" => new EncoderChoice(encoder, type, [], ",format=nv12"),
            "v4l2m2m" => new EncoderChoice(encoder, type, [], ",format=yuv420p"),
            _ => new EncoderChoice(encoder, type, [], string.Empty)
        };
    }
}
