using Baballonia.SDK;
using Microsoft.Extensions.Logging;

namespace Baballonia.OpenCVCapture;

public class OpenCvCaptureFactory(ILoggerFactory loggerFactory) : ICaptureFactory
{
    public Capture Create(string address)
    {
        return new OpenCvCapture(address, loggerFactory.CreateLogger<OpenCvCapture>());
    }

    public bool CanConnect(string address)
    {
        var lowered = address.ToLower();

        // paperudp:// is a dedicated UDP capture module, not an OpenCV URI.
        if (lowered.StartsWith("paperudp://")) return false;

        var serial = lowered.StartsWith("com") ||
                     lowered.StartsWith("/dev/tty") ||
                     lowered.StartsWith("/dev/cu") ||
                     lowered.StartsWith("/dev/ttyacm");;
        if (serial) return false;

        return lowered.StartsWith("/dev/video") ||
               lowered.EndsWith("appsink") ||
               address == "HTC Multimedia Camera" ||
               int.TryParse(address, out _) ||
               Uri.TryCreate(address, UriKind.Absolute, out _);
    }

    public string GetProviderName() => "Normal Camera";
}
