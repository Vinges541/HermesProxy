using System;
using System.Collections;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;

namespace HermesProxy.Benchmarks;

// The allocation hot spots the 2026-09-19 Alterac Valley traces found, each current path next to a
// verbatim Legacy* copy of what it replaced. One logical group per fix, each with its own baseline.
//
//   GetUpdateField  21% of all proxy allocation: enum.ToString() + a string-keyed lookup per field
//   HasAnyFlag      14%: IConvertible boxed both arguments and the enum's value again in ToUInt64
//   UpdateMask      two fresh BitArrays per Values block, now one refilled pair per client
//   PackedGuid      a byte[8] per half of every packed GUID written
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AllocationHotSpotBenchmarks
{
    // What StoreObjectUpdateInternal resolves for one player Values block, give or take.
    private static readonly UnitField[] UnitFields =
    [
        UnitField.UNIT_FIELD_HEALTH, UnitField.UNIT_FIELD_MAXHEALTH, UnitField.UNIT_FIELD_POWER1,
        UnitField.UNIT_FIELD_MAXPOWER1, UnitField.UNIT_FIELD_LEVEL, UnitField.UNIT_FIELD_FACTIONTEMPLATE,
        UnitField.UNIT_FIELD_FLAGS, UnitField.UNIT_FIELD_FLAGS_2, UnitField.UNIT_FIELD_AURASTATE,
        UnitField.UNIT_FIELD_BASEATTACKTIME, UnitField.UNIT_FIELD_BOUNDINGRADIUS, UnitField.UNIT_FIELD_COMBATREACH,
        UnitField.UNIT_FIELD_DISPLAYID, UnitField.UNIT_FIELD_NATIVEDISPLAYID, UnitField.UNIT_FIELD_MOUNTDISPLAYID,
        UnitField.UNIT_FIELD_BYTES_1, UnitField.UNIT_FIELD_BYTES_2, UnitField.UNIT_NPC_FLAGS,
        UnitField.UNIT_FIELD_TARGET, UnitField.UNIT_FIELD_CHARM,
    ];

    private static readonly PlayerField[] PlayerFields =
    [
        PlayerField.PLAYER_FLAGS, PlayerField.PLAYER_GUILDID, PlayerField.PLAYER_GUILDRANK,
        PlayerField.PLAYER_BYTES, PlayerField.PLAYER_BYTES_2, PlayerField.PLAYER_BYTES_3,
        PlayerField.PLAYER_DUEL_TEAM, PlayerField.PLAYER_GUILD_TIMESTAMP, PlayerField.PLAYER_QUEST_LOG_1_1,
        PlayerField.PLAYER_CHOSEN_TITLE,
    ];

    private static readonly ObjectTypeMask[] SectionFlags =
    [
        ObjectTypeMask.Object, ObjectTypeMask.Item, ObjectTypeMask.Container, ObjectTypeMask.Unit,
        ObjectTypeMask.Player, ObjectTypeMask.ActivePlayer, ObjectTypeMask.GameObject,
        ObjectTypeMask.DynamicObject, ObjectTypeMask.Corpse,
    ];

    private Dictionary<string, int> _unitNames = null!;
    private Dictionary<string, int> _playerNames = null!;
    private ObjectTypeMask _playerMask;
    private int[] _maskWords = null!;
    private readonly BitArray _maskScratch = new(0);
    private readonly BitArray _changedScratch = new(0);
    private readonly WowGuid128[] _guids = new WowGuid128[16];
    private WorldPacket _packet = null!;

    [GlobalSetup]
    public void Setup()
    {
        if (VersionBootstrap.LegacyBuild == ClientVersionBuild.Zero)
            VersionBootstrap.LegacyBuild = ClientVersionBuild.V3_3_5a_12340;
        if (VersionBootstrap.ModernBuild == ClientVersionBuild.Zero)
            VersionBootstrap.ModernBuild = ClientVersionBuild.V3_4_3_54261;

        var build = LegacyVersion.GetUpdateFieldsDefiningBuild();
        GeneratedUpdateFieldTables.TryGet(build, typeof(UnitField), out _, out _, out var unitNames);
        GeneratedUpdateFieldTables.TryGet(build, typeof(PlayerField), out _, out _, out var playerNames);
        _unitNames = unitNames!;
        _playerNames = playerNames!;

        _playerMask = ObjectTypeMask.Object | ObjectTypeMask.Unit | ObjectTypeMask.Player;

        // A sparse player delta: 6 words (UNIT_END-ish) with a handful of bits set.
        _maskWords = [0x10, 0, 0x0300_0000, 0, 0, 0x4];

        var rng = new Random(7);
        for (int i = 0; i < _guids.Length; i++)
            _guids[i] = new WowGuid128((ulong)rng.NextInt64() & 0x0000_00FF_FFFF_FFFF, 0x0C00_0000_0000_0000UL | (ulong)rng.Next(1, 4096) << 42);
        _packet = new WorldPacket();

        // Build each per-enum table outside the measurement.
        LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_HEALTH);
        LegacyVersion.GetUpdateField(PlayerField.PLAYER_FLAGS);
    }

    [GlobalCleanup]
    public void Cleanup() => _packet.Dispose();

    // ---- GetUpdateField: 30 lookups, one player Values block's worth ----

    private static int LegacyGetUpdateField<T>(Dictionary<string, int> names, T field) where T : Enum
        => names.TryGetValue(field.ToString(), out int value) ? value : -1;

    [BenchmarkCategory("GetUpdateField"), Benchmark(Baseline = true)]
    public int GetUpdateField_Legacy()
    {
        int sum = 0;
        foreach (var f in UnitFields) sum += LegacyGetUpdateField(_unitNames, f);
        foreach (var f in PlayerFields) sum += LegacyGetUpdateField(_playerNames, f);
        return sum;
    }

    [BenchmarkCategory("GetUpdateField"), Benchmark]
    public int GetUpdateField()
    {
        int sum = 0;
        foreach (var f in UnitFields) sum += LegacyVersion.GetUpdateField(f);
        foreach (var f in PlayerFields) sum += LegacyVersion.GetUpdateField(f);
        return sum;
    }

    // ---- HasAnyFlag: ComputeValuesChangedMask's nine section checks ----

    private static bool LegacyHasAnyFlag(IConvertible value, IConvertible flag) => (value.ToUInt64(null) & flag.ToUInt64(null)) != 0;

    [BenchmarkCategory("HasAnyFlag"), Benchmark(Baseline = true)]
    public uint HasAnyFlag_Legacy()
    {
        uint changed = 0;
        for (int i = 0; i < SectionFlags.Length; i++)
            if (LegacyHasAnyFlag(_playerMask, SectionFlags[i]))
                changed |= 1u << i;
        return changed;
    }

    [BenchmarkCategory("HasAnyFlag"), Benchmark]
    public uint HasAnyFlag()
    {
        uint changed = 0;
        for (int i = 0; i < SectionFlags.Length; i++)
            if (_playerMask.HasAnyFlag(SectionFlags[i]))
                changed |= 1u << i;
        return changed;
    }

    // ---- Update mask: the two BitArrays one Values block needs ----

    private static BitArray LegacyBuildUpdateMask(ReadOnlySpan<int> words, int length)
    {
        var mask = new BitArray(Math.Max(length, words.Length * 32));
        for (int w = 0; w < words.Length; w++)
        {
            uint word = (uint)words[w];
            while (word != 0)
            {
                mask[(w << 5) + BitOperations.TrailingZeroCount(word)] = true;
                word &= word - 1;
            }
        }
        return mask;
    }

    [BenchmarkCategory("UpdateMask"), Benchmark(Baseline = true)]
    public int UpdateMask_Legacy()
    {
        var mask = LegacyBuildUpdateMask(_maskWords, 148);
        var changed = new BitArray(_maskWords.Length * 32);
        return mask.Length + changed.Length;
    }

    [BenchmarkCategory("UpdateMask"), Benchmark]
    public int UpdateMask_Reused()
    {
        WorldClient.FillUpdateMask(_maskScratch, _maskWords, 148);
        _changedScratch.Length = _maskWords.Length * 32;
        _changedScratch.SetAll(false);
        return _maskScratch.Length + _changedScratch.Length;
    }

    // ---- Packed GUID128: 16 GUIDs into a reused packet ----

    private static void LegacyWritePackedGuid128(ByteBuffer buffer, WowGuid128 guid)
    {
        if (guid.IsEmpty())
        {
            buffer.WriteUInt8(0);
            buffer.WriteUInt8(0);
            return;
        }
        uint loSize = LegacyPack(guid.GetLowValue(), out byte lowMask, out byte[] lowPacked);
        uint hiSize = LegacyPack(guid.GetHighValue(), out byte highMask, out byte[] highPacked);
        buffer.WriteUInt8(lowMask);
        buffer.WriteUInt8(highMask);
        buffer.WriteBytes(lowPacked, loSize);
        buffer.WriteBytes(highPacked, hiSize);
    }

    private static uint LegacyPack(ulong value, out byte mask, out byte[] result)
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

    [BenchmarkCategory("PackedGuid"), Benchmark(Baseline = true)]
    public int PackedGuid128_Legacy()
    {
        _packet.Clear();
        foreach (var guid in _guids)
            LegacyWritePackedGuid128(_packet, guid);
        return (int)_packet.GetSize();
    }

    [BenchmarkCategory("PackedGuid"), Benchmark]
    public int PackedGuid128()
    {
        _packet.Clear();
        foreach (var guid in _guids)
            _packet.WritePackedGuid128(guid);
        return (int)_packet.GetSize();
    }
}
