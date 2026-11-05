using Baballonia.SDK;
using Baballonia.VFTCapture.Linux;
using Baballonia.VFTCapture.Windows;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Baballonia.VFTCapture;

public class VFTCaptureFactory(ILoggerFactory loggerFactory) : ICaptureFactory
{
    public Capture Create(string address)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsVftCapture(address, loggerFactory.CreateLogger<WindowsVftCapture>());

        if (OperatingSystem.IsLinux())
            return new LinuxVftCapture(address, loggerFactory.CreateLogger<LinuxVftCapture>());

        throw new InvalidOperationException("Unsupported operating system for VFTCapture.");
    }

    public bool CanConnect(string address)
    {
        // address will be either the device's friendly name, or its numerical index 0, 1, 2...
        var lowered = address.ToLower();

        // Hack - prevent VFTCapture from opening Babble trackers
        if (lowered == "openiristracker") return false;
        else if (OperatingSystem.IsWindows()) return !lowered.StartsWith("com");
        else if (OperatingSystem.IsLinux())
            return lowered.StartsWith("/dev/video") && IsViveFacialTracker(lowered);

        return false;
    }

    /// <summary>
    /// On Linux every UVC camera is exposed as /dev/videoN, so matching the
    /// path alone makes this module claim ordinary webcams. Check the V4L2
    /// device name instead and only accept actual Vive facial trackers.
    /// </summary>
    private static bool IsViveFacialTracker(string devicePath)
    {
        try
        {
            var namePath = Path.Combine("/sys/class/video4linux",
                Path.GetFileName(devicePath), "name");
            if (!File.Exists(namePath))
                return false;

            var name = File.ReadAllText(namePath);
            return name.Contains("Vive", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("HTC", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Multimedia Camera", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string GetProviderName() => "Vive Facial Tracker";
}
