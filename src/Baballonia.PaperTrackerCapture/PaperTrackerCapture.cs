using System.Globalization;
using Baballonia.SDK;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using PaperTracker.Protocol;

namespace Baballonia.PaperTrackerCapture;

/// <summary>
/// Baballonia capture source backed by the PaperTracker UDP stream.
/// Address form: paperudp://0.0.0.0:45454/face  (also /left and /right)
/// </summary>
public sealed class PaperTrackerCapture : Capture
{
    private readonly ILogger _logger;
    private readonly string _bindHost;
    private readonly int _port;
    private readonly int _slot;
    private PaperUdpSession? _session;
    private IDisposable? _subscription;
    private long _decoded;
    private long _failed;

    public PaperTrackerCapture(string source, ILogger logger) : base(source, logger)
    {
        _logger = logger;
        var (bindHost, port, slot) = ParseAddress(source);
        _bindHost = bindHost;
        _port = port;
        _slot = slot;
    }

    public long DecodedFrames => Interlocked.Read(ref _decoded);
    public long FailedFrames => Interlocked.Read(ref _failed);

    public static (string BindHost, int Port, int Slot) ParseAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("paperudp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Not a paperudp address: {address}", nameof(address));
        }

        string host = string.IsNullOrWhiteSpace(uri.Host) ? "0.0.0.0" : uri.Host;
        int port = uri.IsDefaultPort ? PaperUdpSession.DefaultPort : uri.Port;
        string role = uri.AbsolutePath.Trim('/').ToLowerInvariant();
        int slot = role switch
        {
            "" or "face" => 0,
            "left" or "left_eye" or "lefteye" => 1,
            "right" or "right_eye" or "righteye" => 2,
            _ => throw new ArgumentException($"Unknown PaperTracker role '{role}'", nameof(address)),
        };

        return (host, port, slot);
    }

    public override Task<bool> StartCapture()
    {
        if (_subscription != null)
            return Task.FromResult(IsReady);

        try
        {
            _session = PaperUdpSession.Acquire(_bindHost, _port, _logger);
            _subscription = _session.Subscribe(_slot, OnFrame);
            IsReady = true;
            _logger.LogInformation(
                "PaperTracker capture started: {Source} (slot {Slot})", Source, _slot);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PaperTracker capture failed to start for {Source}", Source);
            _session?.Release();
            _session = null;
            IsReady = false;
            return Task.FromResult(false);
        }
    }

    public override Task<bool> StopCapture()
    {
        _subscription?.Dispose();
        _subscription = null;
        _session?.Release();
        _session = null;
        IsReady = false;
        return Task.FromResult(true);
    }

    private void OnFrame(PaperFrame frame)
    {
        try
        {
            var mat = Cv2.ImDecode(frame.Jpeg, ImreadModes.Grayscale);
            if (mat.Empty())
            {
                mat.Dispose();
                Interlocked.Increment(ref _failed);
                return;
            }

            // The host takes ownership of the Mat (it disposes the previous one).
            SetRawMat(mat);
            Interlocked.Increment(ref _decoded);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _logger.LogDebug(ex, "PaperTracker frame decode failed (slot {Slot})", _slot);
        }
    }

    public override void Dispose()
    {
        StopCapture();
        base.Dispose();
    }
}
