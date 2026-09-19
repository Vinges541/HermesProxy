using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// <see cref="UpdateFieldLookup"/> replaced a per-call <c>field.ToString()</c> + name lookup in
/// <c>GetUpdateField</c>. It must resolve every value of every universal field enum, for every
/// build with a generated table, to exactly what the name lookup returned.
/// </summary>
public class UpdateFieldLookupTests
{
    public static TheoryData<ClientVersionBuild> DefiningBuilds =>
    [
        ClientVersionBuild.V1_12_1_5875,
        ClientVersionBuild.V1_14_0_40237,
        ClientVersionBuild.V1_14_1_40688,
        ClientVersionBuild.V2_4_3_8606,
        ClientVersionBuild.V2_5_2_39570,
        ClientVersionBuild.V2_5_3_41750,
        ClientVersionBuild.V3_3_5a_12340,
        ClientVersionBuild.V3_4_3_54261,
    ];

    // The universal enums callers pass to GetUpdateField; per-build enums live in sub-namespaces.
    private static readonly Type[] UniversalFieldEnums = typeof(ObjectField).Assembly.GetTypes()
        .Where(t => t.IsEnum && t.Namespace == typeof(ObjectField).Namespace && t.Name.EndsWith("Field", StringComparison.Ordinal))
        .ToArray();

    private static readonly MethodInfo CompareMethod =
        typeof(UpdateFieldLookupTests).GetMethod(nameof(CompareWithNameLookup), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [MemberData(nameof(DefiningBuilds))]
    public void Build_ResolvesEveryValueLikeTheNameLookup(ClientVersionBuild build)
    {
        Assert.Contains(typeof(UnitField), UniversalFieldEnums);

        int resolved = 0;
        foreach (Type enumType in UniversalFieldEnums)
            resolved += (int)CompareMethod.MakeGenericMethod(enumType).Invoke(null, [build])!;

        // Every build maps at least the object/unit/player basics (V3_4_3, the smallest table,
        // resolves 89); a handful would mean the comparison ran against empty tables and proved
        // nothing.
        Assert.True(resolved > 50, $"only {resolved} fields resolved for {build}");
    }

    [Fact]
    public void GetUpdateField_ForTheBootstrappedBuild_MatchesTheNameLookup()
    {
        var build = LegacyVersion.GetUpdateFieldsDefiningBuild();
        Assert.True(GeneratedUpdateFieldTables.TryGet(build, typeof(UnitField), out _, out _, out var names));

        Assert.Equal(names![nameof(UnitField.UNIT_FIELD_HEALTH)], LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_HEALTH));
        Assert.Equal(-1, LegacyVersion.GetUpdateField((UnitField)int.MaxValue));
    }

    [Fact]
    public void GetUpdateField_AllocatesNothing()
    {
        // JIT and the one-time per-enum table build stay outside the measurement.
        int sum = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_HEALTH) + LegacyVersion.GetUpdateField(PlayerField.PLAYER_FLAGS);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            sum += LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_FLAGS) + LegacyVersion.GetUpdateField(PlayerField.PLAYER_FLAGS);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.NotEqual(int.MinValue, sum);
    }

    /// <returns>How many values resolved to a field.</returns>
    private static int CompareWithNameLookup<T>(ClientVersionBuild build) where T : Enum
    {
        if (!GeneratedUpdateFieldTables.TryGet(build, typeof(T), out _, out _, out Dictionary<string, int>? names))
            names = null;

        var byValue = UpdateFieldLookup.Build<T>(names);

        int resolved = 0;
        foreach (T value in Enum.GetValues(typeof(T)).Cast<T>().Append((T)Enum.ToObject(typeof(T), int.MaxValue)))
        {
            int expected = names != null && names.TryGetValue(value.ToString(), out int f) ? f : -1;
            int actual = byValue.TryGetValue(value, out int g) ? g : -1;
            Assert.True(expected == actual, $"{build} {typeof(T).Name}.{value}: name lookup {expected}, map {actual}");
            if (actual >= 0)
                resolved++;
        }
        return resolved;
    }
}
