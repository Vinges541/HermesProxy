using System;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.SourceGen;

/// <summary>
/// The generated <c>ToStringFast</c> must name every value exactly as Enum.ToString does, and a
/// declared member must cost nothing even right after a GC, which is when Enum.ToString rebuilds
/// its name table.
/// </summary>
public class EnumNameGeneratorTests
{
    [Fact]
    public void EveryOpcode_MatchesEnumToString()
    {
        foreach (Opcode opcode in Enum.GetValues<Opcode>())
            Assert.Equal(opcode.ToString(), opcode.ToStringFast());
    }

    [Fact]
    public void AnUndefinedValue_FallsBackToEnumToString()
    {
        var undefined = (Opcode)0xFFFF_FFF0;
        Assert.False(Enum.IsDefined(undefined));
        Assert.Equal(undefined.ToString(), undefined.ToStringFast());
    }

    [Fact]
    public void ADeclaredMember_AllocatesNothingAfterAGC()
    {
        _ = Opcode.SMSG_UPDATE_OBJECT.ToStringFast();
        GC.Collect(0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string name = Opcode.SMSG_UPDATE_OBJECT.ToStringFast();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("SMSG_UPDATE_OBJECT", name);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AnInterceptedToString_DoesNotRebuildTheNameTable()
    {
        // Server.ResolveOpcodeName names an opcode with a plain ((Opcode)op).ToString(), which the
        // generator intercepts in the HermesProxy assembly. Left alone, the first name after a GC
        // rebuilds all ~3,200 of them, about 280 KB; intercepted, what remains is the IsDefined
        // check beside it. Another test warming the cache concurrently can only lower the number.
        _ = HermesProxy.Server.ResolveOpcodeName((int)Opcode.MSG_NULL_ACTION);
        GC.Collect(0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string name = HermesProxy.Server.ResolveOpcodeName((int)Opcode.CMSG_ABANDON_NPE_RESPONSE);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("CMSG_ABANDON_NPE_RESPONSE", name);
        Assert.True(allocated < 100_000, $"allocated {allocated} B; the name table is being rebuilt");
    }
}
