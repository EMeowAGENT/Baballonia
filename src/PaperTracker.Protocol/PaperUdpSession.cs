using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PaperTracker.Protocol;

public sealed record PaperFrame(byte[] Jpeg, ushort Sequence, DateTimeOffset ReceivedAt);

public sealed record PaperStatus(
    string? Slot,
    bool? Connected,
    string? ConnectedSlots,
    string? AppVersion,
    string? Generation,
    string? AndroidModel);

/// <summary>
/// One process-wide UDP session per (bind address, port). Owns the discovery/keepalive
/// responder and the fragment reassembler, and fans finished frames out to subscribers.
/// </summary>
public sealed class PaperUdpSession : IDisposable
{
    public const int DefaultPort = 45454;
    private static readonly byte[] ProbeBytes = Encoding.ASCII.GetBytes("PTRACKER_PROBE_V1");
    private static readonly byte[] AckBytes = Encoding.ASCII.GetBytes("PTRACKER_ACK_V1");
    private static readonly ConcurrentDictionary<(string Host, int Port), PaperUdpSession> Sessions = new();

    private readonly ILogger _logger;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private int _references;
    private int _disposed;

    private readonly List<Action<PaperFrame>>[] _subscribers = [new(), new(), new()];
    private readonly Dictionary<FrameKey, Assembly> _pending = new();
    private readonly Dictionary<EndPoint, DateTimeOffset> _lastAck = new();
    private readonly PaperStatus?[] _status = new PaperStatus?[3];

    private long _packets;
    private long _frames;
    private long _badFrames;
    private long _dropped;
    private long _duplicates;

    public string BindHost { get; }
    public int Port { get; }

    private PaperUdpSession(string bindHost, int port, ILogger logger)
    {
        BindHost = bindHost;
        Port = port;
        _logger = logger;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _socket.ReceiveBufferSize = 4 * 1024 * 1024;
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Parse(bindHost), port));
        }
        catch (SocketException ex)
        {
            _socket.Dispose();
            throw new InvalidOperationException(
                $"PaperTracker cannot bind UDP {bindHost}:{port}. " +
                "Stop papertrackerd/v4l2loopback (or any other PaperTracker receiver) " +
                "or choose a different port in the camera address.", ex);
        }

        _thread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = $"PaperTrackerUDP:{port}",
        };
        _thread.Start();
        _logger.LogInformation("PaperTracker UDP listening on {Host}:{Port}", bindHost, port);
    }

    /// <summary>
    /// Acquire a shared session. Every successful call must be paired with
    /// <see cref="Release"/>; the socket is closed when the last capture stops.
    /// </summary>
    public static PaperUdpSession Acquire(string bindHost, int port, ILogger logger)
    {
        var key = (bindHost, port);
        var session = Sessions.GetOrAdd(key, k => new PaperUdpSession(k.Host, k.Port, logger));
        Interlocked.Increment(ref session._references);
        return session;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) > 0)
            return;

        if (Sessions.TryRemove((BindHost, Port), out var removed) && ReferenceEquals(removed, this))
            Dispose();
    }

    public IDisposable Subscribe(int slot, Action<PaperFrame> handler)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 2);
        lock (_gate)
        {
            _subscribers[slot].Add(handler);
        }
        return new Subscription(this, slot, handler);
    }

    private void Unsubscribe(int slot, Action<PaperFrame> handler)
    {
        lock (_gate)
        {
            _subscribers[slot].Remove(handler);
        }
    }

    public PaperStatus? GetStatus(int slot) => Volatile.Read(ref _status[slot]);

    public (long Packets, long Frames, long BadFrames, long Dropped, long Duplicates) Stats =>
        (Interlocked.Read(ref _packets), Interlocked.Read(ref _frames),
         Interlocked.Read(ref _badFrames), Interlocked.Read(ref _dropped),
         Interlocked.Read(ref _duplicates));

    private void ReceiveLoop()
    {
        var buffer = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                SocketFlags flags = SocketFlags.None;
                int length = _socket.ReceiveMessageFrom(
                    buffer, 0, buffer.Length, ref flags, ref remote, out var packetInfo);
                if (length <= 0) continue;

                var span = buffer.AsSpan(0, length);
                if (length == ProbeBytes.Length && span.SequenceEqual(ProbeBytes))
                {
                    SendAck(remote, packetInfo);
                    continue;
                }

                if (span[0] == (byte)'{')
                {
                    continue; // lan_network_hint broadcast; we answer probes instead
                }

                if (!PaperPacket.TryParse(span, out var packet))
                {
                    Interlocked.Increment(ref _badFrames);
                    continue;
                }

                Interlocked.Increment(ref _packets);
                HandlePacket(packet);
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PaperTracker UDP receive error");
            }
        }
    }

    private void SendAck(EndPoint remote, IPPacketInformation packetInfo)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastAck.TryGetValue(remote, out var last) && now - last < TimeSpan.FromMilliseconds(150))
        {
            return;
        }
        _lastAck[remote] = now;

        try
        {
            // The headset socket is connect()ed and rejects ACKs that do not
            // originate from the address/port it probed. On Linux the kernel
            // would otherwise pick a source address from the routing table,
            // which is wrong on multi-homed hosts (VPN, several NICs).
            if (OperatingSystem.IsLinux() &&
                remote is IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ep &&
                packetInfo.Address is { AddressFamily: AddressFamily.InterNetwork } local &&
                !local.Equals(IPAddress.Any))
            {
                if (SendWithPacketInfo(ep, local, packetInfo.Interface))
                    return;
            }

            _socket.SendTo(AckBytes, remote);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "PaperTracker ACK failed to {Remote}", remote);
        }
    }

    private const int CmsgHeaderSize = 16; // sizeof(struct cmsghdr) on 64-bit Linux
    private const int InPktInfoSize = 12;  // sizeof(struct in_pktinfo)
    private const int CmsgLen = CmsgHeaderSize + InPktInfoSize;
    private const int CmsgSpace = (CmsgLen + 7) & ~7;

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct SockAddrIn
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public IntPtr iov_base;
        public UIntPtr iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public IntPtr msg_name;
        public uint msg_namelen;
        public IntPtr msg_iov;
        public UIntPtr msg_iovlen;
        public IntPtr msg_control;
        public UIntPtr msg_controllen;
        public int msg_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgHeader
    {
        public UIntPtr cmsg_len;
        public int cmsg_level;
        public int cmsg_type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InPktInfo
    {
        public int ipi_ifindex;
        public uint ipi_spec_dst;
        public uint ipi_addr;
    }

    [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    private static extern nint sendmsg(int sockfd, ref MsgHdr msg, int flags);

    /// <summary>
    /// Send the ACK with IP_PKTINFO so the kernel uses the address the probe
    /// arrived on. This is what keeps the headset's connect()ed UDP socket
    /// happy on machines with more than one IPv4 address.
    /// </summary>
    private bool SendWithPacketInfo(IPEndPoint remote, IPAddress localAddress, int interfaceIndex)
    {
        var remoteBytes = remote.Address.GetAddressBytes();
        var localBytes = localAddress.GetAddressBytes();
        if (remoteBytes.Length != 4 || localBytes.Length != 4)
            return false;

        var address = new SockAddrIn
        {
            sin_family = 2,
            sin_port = (ushort)((remote.Port >> 8) | ((remote.Port & 0xff) << 8)),
            // The struct holds network-order bytes; read them in host order so
            // the in-memory representation stays identical to GetAddressBytes().
            sin_addr = MemoryMarshal.Read<uint>(remoteBytes)
        };

        var destination = Marshal.AllocHGlobal(Marshal.SizeOf<SockAddrIn>());
        var iovStorage = Marshal.AllocHGlobal(Marshal.SizeOf<IoVec>());
        var payload = Marshal.AllocHGlobal(AckBytes.Length);
        var control = Marshal.AllocHGlobal(CmsgSpace);
        try
        {
            Marshal.StructureToPtr(address, destination, false);
            Marshal.Copy(AckBytes, 0, payload, AckBytes.Length);

            var iov = new IoVec
            {
                iov_base = payload,
                iov_len = (UIntPtr)AckBytes.Length
            };
            Marshal.StructureToPtr(iov, iovStorage, false);

            for (var i = 0; i < CmsgSpace; i++)
                Marshal.WriteByte(control, i, 0);

            Marshal.WriteIntPtr(control, 0, (IntPtr)CmsgLen);
            Marshal.WriteInt32(control, IntPtr.Size, 0);      // IPPROTO_IP
            Marshal.WriteInt32(control, IntPtr.Size + 4, 8);  // IP_PKTINFO
            Marshal.WriteInt32(control, CmsgHeaderSize, interfaceIndex);

            var source = MemoryMarshal.Read<uint>(localBytes);
            Marshal.WriteInt32(control, CmsgHeaderSize + 4, unchecked((int)source));
            Marshal.WriteInt32(control, CmsgHeaderSize + 8, unchecked((int)source));

            var msg = new MsgHdr
            {
                msg_name = destination,
                msg_namelen = (uint)Marshal.SizeOf<SockAddrIn>(),
                msg_iov = iovStorage,
                msg_iovlen = (UIntPtr)1,
                msg_control = control,
                msg_controllen = (UIntPtr)CmsgSpace,
                msg_flags = 0
            };

            var sent = sendmsg((int)_socket.Handle, ref msg, 0);
            if (sent == AckBytes.Length)
                return true;

            _logger.LogDebug("PaperTracker sendmsg(IP_PKTINFO) failed: errno={Errno}",
                Marshal.GetLastPInvokeError());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PaperTracker sendmsg(IP_PKTINFO) threw");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(control);
            Marshal.FreeHGlobal(payload);
            Marshal.FreeHGlobal(iovStorage);
            Marshal.FreeHGlobal(destination);
        }
    }

    private void HandlePacket(PaperPacket packet)
    {
        var key = new FrameKey(packet.Slot, packet.FrameType, packet.Sequence);
        Assembly assembly;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out assembly!))
            {
                // New sequence: any older unfinished assembly for this slot/type is dead.
                foreach (var stale in _pending.Keys
                             .Where(k => k.Slot == packet.Slot && k.FrameType == packet.FrameType)
                             .ToArray())
                {
                    _pending.Remove(stale);
                    Interlocked.Increment(ref _dropped);
                }

                assembly = new Assembly(packet.FragmentCount);
                _pending[key] = assembly;
            }

            if (!assembly.Add(packet.FragmentIndex, packet.Payload))
            {
                Interlocked.Increment(ref _duplicates);
                return;
            }

            if (!assembly.IsComplete)
            {
                return;
            }

            _pending.Remove(key);
        }

        byte[] blob = assembly.Assemble();
        Interlocked.Increment(ref _frames);

        if (packet.FrameType == PaperPacket.TypeStatus)
        {
            ParseStatus(packet.Slot, blob);
            return;
        }

        if (blob.Length < 4 || blob[0] != 0xFF || blob[1] != 0xD8 || blob[^2] != 0xFF || blob[^1] != 0xD9)
        {
            Interlocked.Increment(ref _badFrames);
            return;
        }

        var frame = new PaperFrame(blob, packet.Sequence, DateTimeOffset.UtcNow);
        Action<PaperFrame>[] handlers;
        lock (_gate)
        {
            handlers = _subscribers[packet.Slot].ToArray();
        }
        foreach (var handler in handlers)
        {
            try
            {
                handler(frame);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PaperTracker frame handler failed");
            }
        }
    }

    private void ParseStatus(int slot, byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? slotsCsv = null;
            if (root.TryGetProperty("bridge_connected_slots", out var slots) &&
                slots.ValueKind == JsonValueKind.Array)
            {
                slotsCsv = string.Join(",", slots.EnumerateArray().Select(s => s.GetString()));
            }

            var status = new PaperStatus(
                root.TryGetProperty("bridge_slot", out var s) ? s.GetString() : null,
                root.TryGetProperty("bridge_slot_connected", out var c) && c.ValueKind == JsonValueKind.True,
                slotsCsv,
                root.TryGetProperty("bridge_app_version", out var v) ? v.GetString() : null,
                root.TryGetProperty("bridge_tracker_generation", out var g) ? g.GetString() : null,
                root.TryGetProperty("bridge_android_model", out var m) ? m.GetString() : null);

            if (!Equals(Volatile.Read(ref _status[slot]), status))
            {
                Volatile.Write(ref _status[slot], status);
                _logger.LogInformation(
                    "PaperTracker {Slot}: connected={Connected} slots={Slots} app={App} gen={Gen} model={Model}",
                    status.Slot, status.Connected, status.ConnectedSlots, status.AppVersion,
                    status.Generation, status.AndroidModel);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed status frames.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _socket.Dispose();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
        _logger.LogInformation("PaperTracker UDP session {Host}:{Port} closed", BindHost, Port);
    }

    private readonly record struct FrameKey(int Slot, int FrameType, int Sequence);

    private sealed class Assembly(int fragmentCount)
    {
        private readonly byte[]?[] _chunks = new byte[fragmentCount][];
        private int _received;

        public int Count => fragmentCount;
        public bool IsComplete => _received == fragmentCount;

        public bool Add(int index, ReadOnlyMemory<byte> payload)
        {
            if (index < 0 || index >= _chunks.Length) return false;
            if (_chunks[index] is not null) return false;
            _chunks[index] = payload.ToArray();
            _received++;
            return true;
        }

        public byte[] Assemble()
        {
            int total = 0;
            foreach (var chunk in _chunks) total += chunk!.Length;
            var result = new byte[total];
            int offset = 0;
            foreach (var chunk in _chunks)
            {
                chunk!.CopyTo(result, offset);
                offset += chunk.Length;
            }
            return result;
        }
    }

    private sealed class Subscription(PaperUdpSession session, int slot, Action<PaperFrame> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                session.Unsubscribe(slot, handler);
            }
        }
    }
}
