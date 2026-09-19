using System;
using System.Reflection;
using Xunit;

namespace HermesProxy.Tests.Framework;

/// <summary>
/// FlagMappingCache.ToUInt64 unboxes the enum's underlying value directly instead of going
/// through Convert.ToUInt64, which boxed it again. The result, and the exception for a negative
/// signed value, must be exactly what Convert.ToUInt64 gave.
/// </summary>
public class FlagMappingToUInt64Tests
{
    private enum U8 : byte { A = 0x80 }
    private enum U16 : ushort { A = 0x8000 }
    private enum U32 : uint { A = 0x8000_0000 }
    private enum U64 : ulong { A = 0x8000_0000_0000_0000 }
    private enum S8 : sbyte { A = 1 }
    private enum S16 : short { A = 1 }
    private enum S32 { A = 1 }
    private enum S64 : long { A = 1 }

    private static readonly MethodInfo ToUInt64 =
        typeof(Extensions).Assembly.GetType("System.FlagMappingCache")!
            .GetMethod("ToUInt64", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object Call(Enum value)
    {
        try { return ToUInt64.Invoke(null, [value])!; }
        catch (TargetInvocationException e) { return e.InnerException!.GetType(); }
    }

    private static object Reference(Enum value)
    {
        try { return Convert.ToUInt64(value); }
        catch (Exception e) { return e.GetType(); }
    }

    public static TheoryData<Enum> Values =>
    [
        (U8)0, U8.A, (U8)0xFF,
        (U16)0, U16.A, (U16)0xFFFF,
        (U32)0, U32.A, (U32)0xFFFF_FFFF,
        (U64)0, U64.A, (U64)ulong.MaxValue,
        (S8)0, S8.A, (S8)sbyte.MaxValue, (S8)(-1), (S8)sbyte.MinValue,
        (S16)0, S16.A, (S16)short.MaxValue, (S16)(-1),
        (S32)0, S32.A, (S32)int.MaxValue, (S32)(-1), (S32)int.MinValue,
        (S64)0, S64.A, (S64)long.MaxValue, (S64)(-1),
    ];

    [Theory]
    [MemberData(nameof(Values))]
    public void MatchesConvertToUInt64(Enum value)
        => Assert.Equal(Reference(value), Call(value));
}
