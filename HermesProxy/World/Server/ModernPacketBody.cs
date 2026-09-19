using System;
using System.Buffers.Binary;
using Framework.IO;

namespace HermesProxy.World.Server;

/// <summary>
/// Lays out the body of one modern world packet, the part behind the 16-byte header that gets
/// encrypted: the opcode and payload, or the <c>SMSG_COMPRESSED_PACKET</c> envelope around them.
/// </summary>
/// <remarks>
/// Split out of <see cref="WorldSocket.SendPacket"/> so the wire bytes can be tested without a
/// socket. The layout matches TrinityCore's <c>WorldSocket::WritePacketToBuffer</c>.
/// </remarks>
internal static class ModernPacketBody
{
    /// <summary>Bodies with a payload larger than this go out compressed
    /// (TrinityCore's <c>WorldSocket::MinSizeForCompression</c>).</summary>
    public const int MinSizeForCompression = 0x400;

    /// <summary>u32 uncompressed size (opcode included), u32 Adler-32 of opcode + payload,
    /// u32 Adler-32 of the deflated bytes.</summary>
    public const int CompressedInfoSize = 12;

    // The client seeds both checksums with this rather than zlib's 1.
    private const uint AdlerSeed = 0x9827D8F1;

    public static int PlainSize(int payloadLength) => sizeof(ushort) + payloadLength;

    public static int CompressedSize(int deflatedLength) => sizeof(ushort) + CompressedInfoSize + deflatedLength;

    public static void WritePlain(Span<byte> body, ushort opcode, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(body, opcode);
        payload.CopyTo(body[sizeof(ushort)..]);
    }

    /// <param name="deflated">Opcode + payload, deflated on the connection's stream and ending in
    /// a sync-flush marker.</param>
    public static void WriteCompressed(Span<byte> body, ushort compressedOpcode, ushort opcode,
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> deflated)
    {
        Span<byte> opcodeBytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(opcodeBytes, opcode);

        BinaryPrimitives.WriteUInt16LittleEndian(body, compressedOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(body[2..], payload.Length + sizeof(ushort));
        BinaryPrimitives.WriteUInt32LittleEndian(body[6..], Adler32.Update(Adler32.Update(AdlerSeed, opcodeBytes), payload));
        BinaryPrimitives.WriteUInt32LittleEndian(body[10..], Adler32.Update(AdlerSeed, deflated));
        deflated.CopyTo(body[(sizeof(ushort) + CompressedInfoSize)..]);
    }
}
