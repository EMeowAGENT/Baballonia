using Baballonia.Contracts;
using Baballonia.Services.events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Baballonia.Services;

/// <summary>
/// Forwards the raw (post-filter) eye pipeline output to a local WiVRn instance
/// over a small fixed-layout UDP packet.
///
/// Settings are read once on startup, so changing them requires an app restart.
/// </summary>
public sealed class WiVrnGazeSender : IHostedService, IDisposable
{
    public const string EnabledSettingKey = "AppSettings_WiVrnGazeEnabled";
    public const string PortSettingKey = "AppSettings_WiVrnGazePort";
    public const int DefaultPort = 9010;

    private const int PacketSize = 64;
    private const double MinSendIntervalMs = 11.0; // ~90 Hz upper bound
    private const float DegPerUnit = 90.0f;
    private const float LidValidThreshold = 0.1f;

    private static readonly byte[] Magic = "WVGZ"u8.ToArray();

    private readonly IEyePipelineEventBus _eyePipelineEventBus;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<WiVrnGazeSender> _logger;

    private readonly Channel<GazePayload> _channel = Channel.CreateBounded<GazePayload>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<EyePipelineEvents.EyeGazeSampleEvent> _onEyeSample;
    private readonly byte[] _packet = new byte[PacketSize];

    private UdpClient? _udpClient;
    private IPEndPoint? _destination;
    private Task? _sendTask;
    private bool _started;
    private bool _subscribed;
    private long _lastSendTimestamp;
    private long _lastErrorLogTimestamp;
    private bool _disposed;

    // Last combined orientation that passed the lid validity check (identity until then),
    // so packets sent while the eyes are closed keep the previous pose.
    private float _lastQx;
    private float _lastQy;
    private float _lastQz = 0.0f;
    private float _lastQw = 1.0f;

    public WiVrnGazeSender(
        IEyePipelineEventBus eyePipelineEventBus,
        ILocalSettingsService localSettingsService,
        ILogger<WiVrnGazeSender> logger)
    {
        _eyePipelineEventBus = eyePipelineEventBus;
        _localSettingsService = localSettingsService;
        _logger = logger;
        _onEyeSample = OnEyeSample;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        var enabled = _localSettingsService.ReadSetting(EnabledSettingKey, false);
        if (!enabled)
        {
            _logger.LogInformation("WiVRn eye gaze sender disabled");
            return Task.CompletedTask;
        }

        var port = _localSettingsService.ReadSetting(PortSettingKey, DefaultPort);
        if (port is < 1 or > 65535)
        {
            _logger.LogWarning("Invalid WiVRn eye gaze port {Port}, falling back to {DefaultPort}", port, DefaultPort);
            port = DefaultPort;
        }

        try
        {
            _udpClient?.Dispose();
            _udpClient = new UdpClient(AddressFamily.InterNetwork);
            _destination = new IPEndPoint(IPAddress.Loopback, port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create WiVRn eye gaze UDP socket on 127.0.0.1:{Port}", port);
            _udpClient?.Dispose();
            _udpClient = null;
            _destination = null;
            return Task.CompletedTask;
        }

        _eyePipelineEventBus.Subscribe(_onEyeSample);
        _subscribed = true;
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));

        _logger.LogInformation("WiVRn eye gaze sender enabled, sending to 127.0.0.1:{Port}", port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _channel.Writer.TryComplete();
        _cts.Cancel();

        if (_sendTask != null)
        {
            try
            {
                await _sendTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for WiVRn eye gaze send loop to stop");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WiVRn eye gaze send loop ended with an exception");
            }

            _sendTask = null;
        }

        _udpClient?.Dispose();
        _udpClient = null;
        _destination = null;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;

        _eyePipelineEventBus.Unsubscribe(_onEyeSample);
        _subscribed = false;
    }

    /// <summary>
    /// Runs on the eye pipeline thread: only converts and enqueues, never touches the socket.
    /// Sample layout: leftPitch, leftYaw, leftLid, rightPitch, rightYaw, rightLid (0..1).
    /// </summary>
    private void OnEyeSample(EyePipelineEvents.EyeGazeSampleEvent e)
    {
        var sample = e.Sample;
        if (sample is not { Length: >= Utils.EyeRawExpressions })
            return;

        var leftPitch = sample[0];
        var leftYaw = sample[1];
        var leftLid = sample[2];
        var rightPitch = sample[3];
        var rightYaw = sample[4];
        var rightLid = sample[5];

        var combinedYaw = (leftYaw + rightYaw) * 0.5f;
        var combinedPitch = (leftPitch + rightPitch) * 0.5f;

        var yawRad = ToRadians((combinedYaw - 0.5f) * DegPerUnit);
        var pitchRad = ToRadians((combinedPitch - 0.5f) * DegPerUnit);

        // q = q_yaw (around +Y) * q_pitch (around +X)
        var halfYaw = yawRad * 0.5f;
        var halfPitch = pitchRad * 0.5f;
        var sy = MathF.Sin(halfYaw);
        var cy = MathF.Cos(halfYaw);
        var sp = MathF.Sin(halfPitch);
        var cp = MathF.Cos(halfPitch);

        var qx = cy * sp;
        var qy = sy * cp;
        var qz = -sy * sp;
        var qw = cy * cp;

        byte flags = 0;
        if ((leftLid + rightLid) * 0.5f > LidValidThreshold)
            flags |= 0x1; // combined valid
        if (leftLid > LidValidThreshold)
            flags |= 0x2;
        if (rightLid > LidValidThreshold)
            flags |= 0x4;

        if ((flags & 0x1) != 0)
        {
            _lastQx = qx;
            _lastQy = qy;
            _lastQz = qz;
            _lastQw = qw;
        }

        var payload = new GazePayload(
            CurrentMonotonicNanoseconds(),
            _lastQx, _lastQy, _lastQz, _lastQw,
            (leftYaw - 0.5f) * DegPerUnit,
            (leftPitch - 0.5f) * DegPerUnit,
            (rightYaw - 0.5f) * DegPerUnit,
            (rightPitch - 0.5f) * DegPerUnit,
            leftLid,
            rightLid,
            flags);

        // Bounded channel drops the oldest entry when full, so this never blocks the pipeline.
        _channel.Writer.TryWrite(payload);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                // Drain to the newest sample so we don't build up latency.
                var latest = default(GazePayload);
                var hasSample = false;
                while (reader.TryRead(out var payload))
                {
                    latest = payload;
                    hasSample = true;
                }

                if (!hasSample)
                    continue;

                var now = Stopwatch.GetTimestamp();
                if (_lastSendTimestamp != 0)
                {
                    var elapsedMs = (now - _lastSendTimestamp) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs < MinSendIntervalMs)
                    {
                        var delayMs = MinSendIntervalMs - elapsedMs;
                        if (delayMs > 0)
                            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
                    }
                }

                Send(latest);
                _lastSendTimestamp = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WiVRn eye gaze send loop stopped unexpectedly");
        }
    }

    private void Send(in GazePayload payload)
    {
        var client = _udpClient;
        var destination = _destination;
        if (client == null || destination == null)
            return;

        WritePacket(payload);

        try
        {
            client.Send(_packet, _packet.Length, destination);
        }
        catch (Exception ex)
        {
            var now = Stopwatch.GetTimestamp();
            // Throttle error logging to once every ~5 seconds, debug level only.
            if (_lastErrorLogTimestamp == 0 ||
                (now - _lastErrorLogTimestamp) > Stopwatch.Frequency * 5)
            {
                _lastErrorLogTimestamp = now;
                _logger.LogDebug(ex, "Failed to send WiVRn eye gaze packet to {Destination}", destination);
            }
        }
    }

    private void WritePacket(in GazePayload payload)
    {
        var span = _packet.AsSpan();
        span.Clear();

        Magic.CopyTo(span);
        span[4] = 1; // version
        span[5] = payload.Flags;
        // 6..7 reserved = 0

        BinaryPrimitives.WriteUInt64LittleEndian(span[8..16], payload.TimestampNs);

        BinaryPrimitives.WriteSingleLittleEndian(span[16..20], payload.Qx);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..24], payload.Qy);
        BinaryPrimitives.WriteSingleLittleEndian(span[24..28], payload.Qz);
        BinaryPrimitives.WriteSingleLittleEndian(span[28..32], payload.Qw);

        BinaryPrimitives.WriteSingleLittleEndian(span[32..36], payload.LeftYawDeg);
        BinaryPrimitives.WriteSingleLittleEndian(span[36..40], payload.LeftPitchDeg);
        BinaryPrimitives.WriteSingleLittleEndian(span[40..44], payload.RightYawDeg);
        BinaryPrimitives.WriteSingleLittleEndian(span[44..48], payload.RightPitchDeg);

        BinaryPrimitives.WriteSingleLittleEndian(span[48..52], payload.LeftLid);
        BinaryPrimitives.WriteSingleLittleEndian(span[52..56], payload.RightLid);

        // 56..63 reserved = 0
    }

    /// <summary>
    /// CLOCK_MONOTONIC nanoseconds. Stopwatch uses CLOCK_MONOTONIC on Linux; the double
    /// intermediate avoids overflowing a long when scaling by 1e9.
    /// </summary>
    private static ulong CurrentMonotonicNanoseconds()
    {
        var seconds = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        return (ulong)(seconds * 1_000_000_000.0);
    }

    private static float ToRadians(float degrees) => degrees * (MathF.PI / 180.0f);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Unsubscribe();
        _channel.Writer.TryComplete();

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }

        _udpClient?.Dispose();
        _udpClient = null;
        _destination = null;
        _cts.Dispose();
    }

    private readonly record struct GazePayload(
        ulong TimestampNs,
        float Qx,
        float Qy,
        float Qz,
        float Qw,
        float LeftYawDeg,
        float LeftPitchDeg,
        float RightYawDeg,
        float RightPitchDeg,
        float LeftLid,
        float RightLid,
        byte Flags);
}
