using System;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.Framework;

/// <summary>
/// The same-type <c>HasAnyFlag</c> overloads replaced one that took <c>IConvertible</c> and boxed
/// every argument. They must answer exactly what it answered, for every enum width.
/// </summary>
public class HasAnyFlagTests
{
    [Flags] private enum Flags8 : byte { A = 0x01, B = 0x80 }
    [Flags] private enum Flags16 : ushort { A = 0x0001, B = 0x8000 }
    [Flags] private enum Flags32 : uint { A = 0x00000001, B = 0x80000000 }
    [Flags] private enum Flags64 : ulong { A = 1, B = 0x8000_0000_0000_0000 }
    [Flags] private enum FlagsSigned32 { A = 0x00000001, B = 0x40000000 }

    // What the removed IConvertible overload computed.
    private static bool Reference(IConvertible value, IConvertible flag) => (value.ToUInt64(null) & flag.ToUInt64(null)) != 0;

    private static readonly ulong[] Samples =
        [0, 1, 2, 3, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x1_0000, 0x4000_0000, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 0x1_0000_0000, 0x8000_0000_0000_0000, ulong.MaxValue];

    [Fact]
    public void EnumOverload_MatchesTheIConvertibleResult_ForEveryWidth()
    {
        foreach (ulong v in Samples)
        foreach (ulong f in Samples)
        {
            Assert.Equal(Reference((Flags8)(byte)v, (Flags8)(byte)f), ((Flags8)(byte)v).HasAnyFlag((Flags8)(byte)f));
            Assert.Equal(Reference((Flags16)(ushort)v, (Flags16)(ushort)f), ((Flags16)(ushort)v).HasAnyFlag((Flags16)(ushort)f));
            Assert.Equal(Reference((Flags32)(uint)v, (Flags32)(uint)f), ((Flags32)(uint)v).HasAnyFlag((Flags32)(uint)f));
            Assert.Equal(Reference((Flags64)v, (Flags64)f), ((Flags64)v).HasAnyFlag((Flags64)f));

            // A signed enum only has a defined IConvertible answer for non-negative values.
            int sv = (int)(v & 0x7FFF_FFFF), sf = (int)(f & 0x7FFF_FFFF);
            Assert.Equal(Reference((FlagsSigned32)sv, (FlagsSigned32)sf), ((FlagsSigned32)sv).HasAnyFlag((FlagsSigned32)sf));
        }
    }

    [Fact]
    public void PrimitiveOverloads_MatchTheIConvertibleResult()
    {
        foreach (ulong v in Samples)
        foreach (ulong f in Samples)
        {
            Assert.Equal(Reference((byte)v, (byte)f), ((byte)v).HasAnyFlag((byte)f));
            Assert.Equal(Reference((ushort)v, (ushort)f), ((ushort)v).HasAnyFlag((ushort)f));
            Assert.Equal(Reference((uint)v, (uint)f), ((uint)v).HasAnyFlag((uint)f));
            Assert.Equal(Reference(v, f), v.HasAnyFlag(f));
            Assert.Equal(Reference((int)(v & 0x7FFF_FFFF), (int)(f & 0x7FFF_FFFF)), ((int)(v & 0x7FFF_FFFF)).HasAnyFlag((int)(f & 0x7FFF_FFFF)));
        }
    }

    [Fact]
    public void MixedWidthCallSites_CastTheConstant_WithoutChangingTheAnswer()
    {
        // Call sites that tested a ushort field against a ushort enum constant, or a uint field
        // against a narrower one, now cast the constant to the field's type.
        foreach (ulong v in Samples)
        {
            ushort auraFlags = (ushort)v;
            Assert.Equal(Reference(auraFlags, AuraFlagsWotLK.Negative), auraFlags.HasAnyFlag((ushort)AuraFlagsWotLK.Negative));

            uint extra = (uint)v;
            Assert.Equal(Reference(extra, MovementFlagExtra.InterpolateMove), extra.HasAnyFlag((uint)MovementFlagExtra.InterpolateMove));
        }
    }

    [Fact]
    public void EnumOverload_AllocatesNothing()
    {
        var mask = ObjectTypeMask.Unit | ObjectTypeMask.Player;
        bool any = mask.HasAnyFlag(ObjectTypeMask.Player); // JIT outside the measurement

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            any ^= mask.HasAnyFlag(ObjectTypeMask.Corpse) | mask.HasAnyFlag(ObjectTypeMask.Unit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(any || !any);
    }
}
