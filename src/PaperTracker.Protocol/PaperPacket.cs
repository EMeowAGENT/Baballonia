using System.Buffers.Binary;

namespace PaperTracker.Protocol;

/// <summary>
/// PaperTracker UDP packet format, confirmed against app 2.4.12 / firmware 23.
/// 16-byte header, big endian:
///   0..1  "PT"
///   2     version (1)
///   3     frameType (1=JPEG, 2=status JSON)
///   4     slot (0=face, 1=left eye, 2=right eye)
///   5     reserved
///   6..7  seq
///   8..9  fragCount
///   10..11 fragIndex
///   12..13 payloadLen
///   14..15 reserved
/// </summary>
public readonly struct PaperPacket
{
    public const int HeaderSize = 16;
    public const int MaxPayload = 1184;
    public const byte Version = 1;
    public const byte TypeJpeg = 1;
    public const byte TypeStatus = 2;

    public readonly byte FrameType;
    public readonly byte Slot;
    public readonly ushort Sequence;
    public readonly ushort FragmentCount;
    public readonly ushort FragmentIndex;
    public readonly ushort PayloadLength;
    public readonly ReadOnlyMemory<byte> Payload;

    private PaperPacket(byte frameType, byte slot, ushort seq, ushort count, ushort index,
        ushort length, ReadOnlyMemory<byte> payload)
    {
        FrameType = frameType;
        Slot = slot;
        Sequence = seq;
        FragmentCount = count;
        FragmentIndex = index;
        PayloadLength = length;
        Payload = payload;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out PaperPacket packet)
    {
        packet = default;
        if (data.Length < HeaderSize) return false;
        if (data[0] != (byte)'P' || data[1] != (byte)'T') return false;
        if (data[2] != Version) return false;
        if (data[5] != 0 || data[14] != 0 || data[15] != 0) return false;

        byte frameType = data[3];
        byte slot = data[4];
        if (frameType is not (TypeJpeg or TypeStatus)) return false;
        if (slot > 2) return false;

        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        ushort index = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);

        if (count == 0 || index >= count) return false;
        if (length != data.Length - HeaderSize) return false;
        if (length > MaxPayload) return false;

        packet = new PaperPacket(frameType, slot, seq, count, index, length,
            data[HeaderSize..].ToArray());
        return true;
    }
}
