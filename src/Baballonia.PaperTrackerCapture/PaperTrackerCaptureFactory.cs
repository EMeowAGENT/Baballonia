using Baballonia.SDK;
using Microsoft.Extensions.Logging;

namespace Baballonia.PaperTrackerCapture;

public sealed class PaperTrackerCaptureFactory : ICaptureFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public PaperTrackerCaptureFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public Capture Create(string address) =>
        new PaperTrackerCapture(address, _loggerFactory.CreateLogger<PaperTrackerCapture>());

    public bool CanConnect(string address) =>
        address.StartsWith("paperudp://", StringComparison.OrdinalIgnoreCase);

    public string GetProviderName() => "PaperTracker UDP";
}
