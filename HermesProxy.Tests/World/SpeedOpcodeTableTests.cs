using System;
using System.Linq;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// The speed handlers' opcode tables replaced a per-packet rename (ToString + Replace + TryParse).
/// Every entry must be what the rename produced, and every handled opcode must have an entry.
/// </summary>
public class SpeedOpcodeTableTests
{
    [Fact]
    public void ForceToSet_MatchesTheRename()
    {
        Assert.Equal(9, WorldClient.SpeedOpcodes.ForceToSet.Count);
        foreach (var (from, to) in WorldClient.SpeedOpcodes.ForceToSet)
        {
            Opcode renamed = Opcodes.GetUniversalOpcode(from.ToString().Replace("SMSG_FORCE_", "SMSG_MOVE_SET_").Replace("_CHANGE", ""));
            Assert.Equal(renamed, to);
            Assert.NotEqual(Opcode.MSG_NULL_ACTION, to);
        }
    }

    [Fact]
    public void SetToUpdate_MatchesTheRename()
    {
        Assert.Equal(9, WorldClient.SpeedOpcodes.SetToUpdate.Count);
        foreach (var (from, to) in WorldClient.SpeedOpcodes.SetToUpdate)
        {
            Assert.Equal(Opcodes.GetUniversalOpcode(from.ToString().Replace("MSG_MOVE_SET", "SMSG_MOVE_UPDATE")), to);
            Assert.NotEqual(Opcode.MSG_NULL_ACTION, to);
        }
    }

    [Fact]
    public void SwimToFlight_MatchesTheRename()
    {
        foreach (var (from, to) in WorldClient.SpeedOpcodes.SwimToFlight)
            Assert.Equal(Enum.Parse<Opcode>(from.ToString().Replace("SWIM", "FLIGHT")), to);

        // Both handlers look the swim opcodes up here, so each must be present.
        Opcode[] swim = [Opcode.SMSG_MOVE_SET_SWIM_SPEED, Opcode.SMSG_MOVE_SET_SWIM_BACK_SPEED,
                         Opcode.SMSG_MOVE_UPDATE_SWIM_SPEED, Opcode.SMSG_MOVE_UPDATE_SWIM_BACK_SPEED];
        Assert.All(swim, o => Assert.True(WorldClient.SpeedOpcodes.SwimToFlight.ContainsKey(o)));
    }

    [Fact]
    public void Renamed_OutsideTheTable_FallsBackToTheRename()
    {
        Opcode result = WorldClient.SpeedOpcodes.Renamed(
            WorldClient.SpeedOpcodes.ForceToSet, Opcode.MSG_NULL_ACTION, WorldClient.SpeedOpcodes.ForceToSetName);
        Assert.Equal(Opcodes.GetUniversalOpcode(WorldClient.SpeedOpcodes.ForceToSetName(nameof(Opcode.MSG_NULL_ACTION))), result);
    }

    [Fact]
    public void EveryHandledOpcode_HasAnEntry()
    {
        // The [HandlesSmsg] lists on the two handlers are the table keys.
        var handlers = typeof(WorldClient).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(m => m.Name is "HandleMoveForceSpeedChange" or "HandleMoveUpdateSpeed");
        foreach (var method in handlers)
        {
            var table = method.Name == "HandleMoveForceSpeedChange" ? WorldClient.SpeedOpcodes.ForceToSet : WorldClient.SpeedOpcodes.SetToUpdate;
            foreach (var attr in method.GetCustomAttributes(typeof(HermesProxy.World.Dispatch.HandlesSmsgAttribute), false).Cast<HermesProxy.World.Dispatch.HandlesSmsgAttribute>())
                Assert.True(table.ContainsKey(attr.Opcode), $"{method.Name} handles {attr.Opcode} but its table has no entry");
        }
    }
}
