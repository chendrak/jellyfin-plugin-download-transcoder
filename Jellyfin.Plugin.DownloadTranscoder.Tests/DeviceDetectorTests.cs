using Jellyfin.Plugin.DownloadTranscoder.Configuration;
using Xunit;

namespace Jellyfin.Plugin.DownloadTranscoder.Tests;

public class DeviceDetectorTests
{
    [Theory]
    [InlineData("Jellyfin Android", "Pixel 8 Pro", "AndroidDownloadManager/14 (Linux; U; Android 14; Pixel 8 Pro Build/AP2A)", DeviceKind.Phone)]
    [InlineData("Jellyfin Android", "SM-S918B", "", DeviceKind.Phone)]
    [InlineData("Jellyfin Android", "SM-X710", "", DeviceKind.Tablet)]
    [InlineData("Jellyfin Android", "Pixel Tablet", "", DeviceKind.Tablet)]
    [InlineData("Jellyfin Android", "Lenovo TB-X606F", "", DeviceKind.Tablet)]
    [InlineData("Jellyfin Android", "KFTRWI", "", DeviceKind.Tablet)]
    [InlineData("Jellyfin Web", "Chrome Android", "Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36", DeviceKind.Tablet)]
    [InlineData("Jellyfin Web", "Chrome Android", "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36", DeviceKind.Phone)]
    [InlineData("Jellyfin iOS", "iPhone", "", DeviceKind.Phone)]
    [InlineData("Swiftfin iOS", "iPad", "", DeviceKind.Tablet)]
    [InlineData("Jellyfin Android TV", "SHIELD Android TV", "", DeviceKind.Tv)]
    [InlineData("Jellyfin Android TV", "AFTKA", "", DeviceKind.Tv)]
    [InlineData("Swiftfin tvOS", "Apple TV", "", DeviceKind.Tv)]
    [InlineData("Jellyfin Web", "Chrome", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36", DeviceKind.Desktop)]
    [InlineData("Jellyfin Media Player", "my-pc", "", DeviceKind.Desktop)]
    [InlineData("Some Script", "server", "curl/8.5", DeviceKind.Other)]
    public void DetectsDeviceKind(string client, string device, string userAgent, DeviceKind expected)
    {
        Assert.Equal(expected, DeviceDetector.Detect(client, device, userAgent));
    }

    [Fact]
    public void ProfileSelectionOrder()
    {
        var cfg = new PluginConfiguration();
        Assert.Equal("Phone", DeviceDetector.SelectProfile(cfg, "dev1", "Jellyfin Android", "Pixel 8", null)!.Name);
        Assert.Equal("Tablet", DeviceDetector.SelectProfile(cfg, "dev2", "Jellyfin Android", "SM-X710", null)!.Name);
        Assert.Equal("Computer", DeviceDetector.SelectProfile(cfg, "dev3", "Jellyfin Web", "Firefox", "Mozilla/5.0 (X11; Linux x86_64) Firefox/130.0")!.Name);
        Assert.Equal("Other", DeviceDetector.SelectProfile(cfg, "dev4", "Some Script", "server", "curl/8.5")!.Name);

        // Name match beats detection.
        cfg.Profiles[1].MatchNames = "Pixel 8";
        Assert.Equal("Tablet", DeviceDetector.SelectProfile(cfg, "dev1", "Jellyfin Android", "Pixel 8", null)!.Name);

        // Explicit assignment beats everything.
        cfg.DeviceAssignments = [new DeviceAssignment { DeviceId = "dev1", Profile = "TV" }];
        Assert.Equal("TV", DeviceDetector.SelectProfile(cfg, "dev1", "Jellyfin Android", "Pixel 8", null)!.Name);

        // Assignment to a deleted profile falls back to detection.
        cfg.DeviceAssignments = [new DeviceAssignment { DeviceId = "dev1", Profile = "Gone" }];
        Assert.Equal("Tablet", DeviceDetector.SelectProfile(cfg, "dev1", "Jellyfin Android", "Pixel 8", null)!.Name);
    }

    [Fact]
    public void ConfigurationRoundTripsThroughXml()
    {
        var cfg = new PluginConfiguration();
        cfg.Profiles[0].MaxHeight = 480;
        cfg.DeviceAssignments = [new DeviceAssignment { DeviceId = "abc", Profile = "Tablet" }];
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var stream = new System.IO.MemoryStream();
        serializer.Serialize(stream, cfg);
        stream.Position = 0;
        var back = (PluginConfiguration)serializer.Deserialize(stream)!;
        Assert.Equal(5, back.Profiles.Length);
        Assert.Equal(480, back.Profiles[0].MaxHeight);
        Assert.Equal(DeviceKind.Tv, back.Profiles[2].DeviceKind);
        Assert.Equal("Tablet", back.DeviceAssignments[0].Profile);
    }
}
