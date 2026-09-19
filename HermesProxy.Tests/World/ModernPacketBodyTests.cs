using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// Wire cover for <see cref="ModernPacketBody"/>, which replaced the two-ByteBuffer body build in
/// <c>WorldSocket.SendPacket</c>. The bytes must be identical to what that code produced, and a
/// compressed body must decode the way the client decodes it: one inflater for the whole
/// connection, checksums seeded with 0x9827D8F1.
/// </summary>
public class ModernPacketBodyTests
{
    private const ushort CompressedOpcode = 0x3052;
    private const uint AdlerSeed = 0x9827D8F1;

    [Fact]
    public void V3_4_3_MapsCompressedPacketToNativeOpcode()
    {
        // Native 3.4.3 (TrinityCore wotlk_classic / Wrathion Opcodes.h) sends SMSG_COMPRESSED_PACKET
        // as 0x3052, the same value 1.14 and 2.5 use. Unmapped, WorldSocket never compresses.
        Assert.True(GeneratedOpcodeTables.TryGet(ClientVersionBuild.V3_4_3_54261, out _, out uint[] universalToCurrent));
        Assert.Equal(0x3052u, universalToCurrent[(int)Opcode.SMSG_COMPRESSED_PACKET]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(1025)]
    public void WritePlain_MatchesPreviousByteBufferBuild(int payloadLength)
    {
        byte[] payload = Payload(payloadLength, seed: 7);
        const ushort opcode = 0x27CB;

        byte[] expected;
        using (ByteBuffer body = new())
        {
            body.WriteUInt16(opcode);
            body.WriteBytes(payload);
            expected = body.GetData();
        }

        byte[] actual = new byte[ModernPacketBody.PlainSize(payload.Length)];
        ModernPacketBody.WritePlain(actual, opcode, payload);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1025)]
    [InlineData(4096)]
    [InlineData(70_000)]
    public void WriteCompressed_MatchesPreviousByteBufferBuild(int payloadLength)
    {
        byte[] payload = Payload(payloadLength, seed: 11);
        const ushort opcode = 0x27CB;
        byte[] deflated = DeflateOne(new DeflateSession(), opcode, payload);

        // The envelope exactly as WorldSocket.SendPacket built it before this class existed.
        byte[] expected;
        using (ByteBuffer compressed = new())
        {
            compressed.WriteInt32(payload.Length + 2);
            Span<byte> opcodeBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(opcodeBytes, opcode);
            compressed.WriteUInt32(Adler32.Update(Adler32.Update(AdlerSeed, opcodeBytes), payload));
            compressed.WriteUInt32(Adler32.Update(AdlerSeed, deflated));
            compressed.WriteBytes(deflated);
            byte[] envelope = compressed.GetData();

            using ByteBuffer body = new();
            body.WriteUInt16(CompressedOpcode);
            body.WriteBytes(envelope);
            expected = body.GetData();
        }

        byte[] actual = new byte[ModernPacketBody.CompressedSize(deflated.Length)];
        ModernPacketBody.WriteCompressed(actual, CompressedOpcode, opcode, payload, deflated);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteCompressed_EnvelopeCarriesSizeAndSeededChecksums()
    {
        byte[] payload = Payload(2048, seed: 3);
        const ushort opcode = 0x2DD4;
        byte[] deflated = DeflateOne(new DeflateSession(), opcode, payload);

        byte[] body = new byte[ModernPacketBody.CompressedSize(deflated.Length)];
        ModernPacketBody.WriteCompressed(body, CompressedOpcode, opcode, payload, deflated);

        byte[] opcodeAndPayload = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(opcodeAndPayload, opcode);
        payload.CopyTo(opcodeAndPayload, 2);

        Assert.Equal(CompressedOpcode, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal(payload.Length + 2, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2)));
        Assert.Equal(ReferenceAdler(AdlerSeed, opcodeAndPayload), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
        Assert.Equal(ReferenceAdler(AdlerSeed, deflated), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));
        Assert.Equal(deflated, body.AsSpan(14).ToArray());
    }

    [Fact]
    public void CompressedBodies_DecodeOnOneClientSideInflater()
    {
        // The client keeps one inflate stream per connection, so each body only decodes after
        // every body before it. Mixed sizes and opcodes, one deflate session, like WorldSocket.
        var session = new DeflateSession();
        (ushort Opcode, byte[] Payload)[] packets =
        [
            (0x27CB, Payload(1500, seed: 1)),
            (0x2DD4, Payload(1025, seed: 2)),
            (0x27CB, Payload(1500, seed: 1)),   // repeat: exercises back-references across packets
            (0x2C1F, Payload(9000, seed: 4)),
        ];

        using var wire = new MemoryStream();
        foreach (var (opcode, payload) in packets)
        {
            byte[] deflated = DeflateOne(session, opcode, payload);
            byte[] body = new byte[ModernPacketBody.CompressedSize(deflated.Length)];
            ModernPacketBody.WriteCompressed(body, CompressedOpcode, opcode, payload, deflated);

            int deflatedLength = body.Length - 14;
            Assert.True(body.AsSpan(body.Length - 4).SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x00, 0xFF, 0xFF]),
                "each body must end on a sync-flush boundary or the client cannot decode it yet");
            wire.Write(body, 14, deflatedLength);
        }

        wire.Position = 0;
        using var inflater = new DeflateStream(wire, CompressionMode.Decompress);
        foreach (var (opcode, payload) in packets)
        {
            byte[] got = new byte[2 + payload.Length];
            inflater.ReadExactly(got);
            Assert.Equal(opcode, BinaryPrimitives.ReadUInt16LittleEndian(got));
            Assert.Equal(payload, got.AsSpan(2).ToArray());
        }
    }

    /// <summary>What WorldSocket keeps per connection: one deflater, sync-flushed per packet.</summary>
    private sealed class DeflateSession
    {
        public readonly MemoryStream Buffer = new();
        public readonly DeflateStream Stream;

        public DeflateSession() => Stream = new DeflateStream(Buffer, CompressionLevel.Fastest, leaveOpen: true);
    }

    private static byte[] DeflateOne(DeflateSession session, ushort opcode, ReadOnlySpan<byte> payload)
    {
        session.Buffer.SetLength(0);
        Span<byte> hdr = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, opcode);
        session.Stream.Write(hdr);
        session.Stream.Write(payload);
        session.Stream.Flush();
        return session.Buffer.ToArray();
    }

    // Compressible but not trivial: update-object-like runs of small values.
    private static byte[] Payload(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(i % 16 < 12 ? rng.Next(4) : rng.Next(256));
        return bytes;
    }

    // Textbook Adler-32, independent of Framework.IO.Adler32.
    private static uint ReferenceAdler(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF, b = adler >> 16;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
