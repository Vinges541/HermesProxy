using System;
using Framework.IO;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// <see cref="WorldPacket.WritePackedGuid128"/> and <see cref="WorldPacket.WritePackedUInt64"/>
/// now pack on the stack. They must write the bytes the byte[]-per-half version wrote, including
/// after pending bits.
/// </summary>
public class PackedGuidWriteTests
{
    // The previous implementation, verbatim apart from writing into a caller's buffer.
    private static void ReferencePackedUInt64(ByteBuffer buffer, ulong value)
    {
        uint size = ReferencePack(value, out byte mask, out byte[] packed);
        buffer.WriteUInt8(mask);
        buffer.WriteBytes(packed, size);
    }

    private static void ReferencePackedGuid128(ByteBuffer buffer, WowGuid128 guid)
    {
        if (guid.IsEmpty())
        {
            buffer.WriteUInt8(0);
            buffer.WriteUInt8(0);
            return;
        }
        uint loSize = ReferencePack(guid.GetLowValue(), out byte lowMask, out byte[] lowPacked);
        uint hiSize = ReferencePack(guid.GetHighValue(), out byte highMask, out byte[] highPacked);
        buffer.WriteUInt8(lowMask);
        buffer.WriteUInt8(highMask);
        buffer.WriteBytes(lowPacked, loSize);
        buffer.WriteBytes(highPacked, hiSize);
    }

    private static uint ReferencePack(ulong value, out byte mask, out byte[] result)
    {
        uint resultSize = 0;
        mask = 0;
        result = new byte[8];
        for (byte i = 0; value != 0; ++i)
        {
            if ((value & 0xFF) != 0)
            {
                mask |= (byte)(1 << i);
                result[resultSize++] = (byte)(value & 0xFF);
            }
            value >>= 8;
        }
        return resultSize;
    }

    private static readonly ulong[] Values =
        [0, 1, 0xFF, 0x100, 0x1234, 0xFF00FF00, 0x0000_0100_0000_0000, 0x8000_0000_0000_0000, ulong.MaxValue, 0x0102_0304_0506_0708];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritePackedGuid128_WritesTheSameBytes(bool pendingBits)
    {
        foreach (ulong low in Values)
        foreach (ulong high in Values)
        {
            var guid = new WowGuid128(low, high);
            using var expected = new WorldPacket();
            using var actual = new WorldPacket();
            if (pendingBits)
            {
                expected.WriteBit(true);
                actual.WriteBit(true);
            }

            ReferencePackedGuid128(expected, guid);
            actual.WritePackedGuid128(guid);

            Assert.Equal(expected.GetData(), actual.GetData());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritePackedUInt64_WritesTheSameBytes(bool pendingBits)
    {
        foreach (ulong value in Values)
        {
            using var expected = new WorldPacket();
            using var actual = new WorldPacket();
            if (pendingBits)
            {
                expected.WriteBit(true);
                actual.WriteBit(true);
            }

            ReferencePackedUInt64(expected, value);
            actual.WritePackedUInt64(value);

            Assert.Equal(expected.GetData(), actual.GetData());
        }
    }
}
