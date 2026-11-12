using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Queries;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DownloadTranscoder.Api;

/// <summary>
/// Shows which profile each known device gets, for the settings page.
/// </summary>
[ApiController]
[Route("DownloadTranscoder")]
[Authorize(Policy = Policies.RequiresElevation)]
public class DevicesController : ControllerBase
{
    private readonly IDeviceManager _deviceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevicesController"/> class.
    /// </summary>
    /// <param name="deviceManager">Device manager.</param>
    public DevicesController(IDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    /// <summary>
    /// Lists known devices with their detected kind and the profile their downloads use.
    /// </summary>
    /// <returns>The devices.</returns>
    [HttpGet("Devices")]
    public ActionResult<IEnumerable<DeviceProfileInfo>> GetDevices()
    {
        var cfg = Plugin.Instance!.Configuration;
        var devices = _deviceManager.GetDeviceInfos(new DeviceQuery()).Items;
        return Ok(devices
            .Where(d => d.Id is not null)
            .OrderByDescending(d => d.DateLastActivity)
            .Select(d => new DeviceProfileInfo
            {
                Id = d.Id!,
                Name = string.IsNullOrEmpty(d.CustomName) ? d.Name ?? string.Empty : d.CustomName,
                AppName = d.AppName ?? string.Empty,
                LastUserName = d.LastUserName ?? string.Empty,
                DetectedKind = DeviceDetector.Detect(d.AppName, d.Name, null).ToString(),
                Profile = Describe(DeviceDetector.SelectProfile(cfg, d.Id, d.AppName, d.Name, null))
            })
            .ToList());
    }

    private static string Describe(Configuration.TranscodeProfile? profile)
        => profile is null ? "None (original file)"
            : profile.Transcode ? profile.Name
            : profile.Name + " (original file)";
}

/// <summary>
/// A device and the profile it gets.
/// </summary>
public class DeviceProfileInfo
{
    /// <summary>Gets or sets the device id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the app name.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Gets or sets the last user.</summary>
    public string LastUserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected kind.</summary>
    public string DetectedKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile that applies.</summary>
    public string Profile { get; set; } = string.Empty;
}
