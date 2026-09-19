using HermesProxy.World;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// ServerPacket keeps its serialized bytes in a pooled array from WritePacketData until the send
/// path releases them, instead of an exact-size copy. A packet sent again must serialize to the
/// same bytes, on both the span path and the ByteBuffer path.
/// </summary>
public class ServerPacketDataTests
{
    private static PowerUpdate SpanPacket()
    {
        var packet = new PowerUpdate(new WowGuid128(0x1234, 0x0C00_0000_0000_0042));
        packet.Powers.Add(new PowerUpdatePower(1234, 0));
        packet.Powers.Add(new PowerUpdatePower(56, 3));
        return packet;
    }

    private static CriteriaDeletedPkt ByteBufferPacket() => new() { CriteriaID = 42 };

    public static TheoryData<string> Kinds => ["span", "bytebuffer"];

    private static ServerPacket Make(string kind) => kind == "span" ? SpanPacket() : ByteBufferPacket();

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ReleasedThenWrittenAgain_SerializesTheSameBytes(string kind)
    {
        var packet = Make(kind);
        packet.WritePacketData();
        byte[] first = packet.GetDataSpan().ToArray();
        Assert.NotEmpty(first);

        packet.ReleaseData();
        Assert.True(packet.GetDataSpan().IsEmpty);

        packet.WritePacketData();
        Assert.Equal(first, packet.GetDataSpan().ToArray());
        packet.ReleaseData();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void GetData_IsACopyOfTheSpan(string kind)
    {
        var packet = Make(kind);
        packet.WritePacketData();

        byte[]? copy = packet.GetData();
        Assert.NotNull(copy);
        Assert.Equal(packet.GetDataSpan().ToArray(), copy);
        packet.ReleaseData();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Discard_AfterWriting_ReleasesTheBytes(string kind)
    {
        var packet = Make(kind);
        packet.WritePacketData();

        packet.Discard();

        Assert.True(packet.GetDataSpan().IsEmpty);
        Assert.Null(packet.GetData());
        packet.Discard(); // idempotent
    }
}
